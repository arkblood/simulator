/**
 * Copyright (c) 2020 LG Electronics, Inc.
 *
 * This software contains code licensed as described in LICENSE.
 *
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Simulator.Bridge;
using Simulator.Utilities;
using Simulator.Sensors.UI;
using Simulator.PointCloud;
using PointCloudData = Simulator.Bridge.Data.PointCloudData;

namespace Simulator.Sensors
{
    public abstract class LidarSensorBase : SensorBase
    {
        // Lidar x is forward, y is left, z is up
        public static readonly Matrix4x4 LidarTransform = new Matrix4x4(new Vector4(0, -1, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(1, 0, 0, 0), Vector4.zero);

        [HideInInspector]
        public int TemplateIndex;

        protected int CurrentLaserCount;
        protected int CurrentMeasurementsPerRotation;
        float CurrentFieldOfView;
        List<float> CurrentVerticalRayAngles;
        float CurrentCenterAngle;
        float CurrentMinDistance;
        float CurrentMaxDistance;

        // Horizontal FOV of the camera
        protected const float HorizontalAngleLimit = 15.0f;

        // List vertical angles for each ray.
        //
        // For models with uniformly distributed vertical angles,
        // this list should be empty (i.e. Count == 0).
        // For models with non-uniformly distributed vertical angles,
        // this Count of this list should equals to LaserCount.
        // Angle values follows Velodyne's convetion:
        // 0 means horizontal;
        // Positive means tilting up; 
        // Negative means tilting down.
        //
        // Refer to LidarSensorEditor.cs for more details of the relationship
        // between VerticalRayAngles and LaserCount/FieldOfView/CenterAngle.
        [SensorParameter]
        public List<float> VerticalRayAngles;

        [SensorParameter]
        [Range(1, 128)]
        public int LaserCount = 32;

        [SensorParameter]
        [Range(1.0f, 45.0f)]
        public float FieldOfView = 40.0f;

        [SensorParameter]
        [Range(-45.0f, 45.0f)]
        public float CenterAngle = 10.0f;

        [SensorParameter]
        [Range(0.01f, 1000f)]
        public float MinDistance = 0.5f; // meters

        [SensorParameter]
        [Range(0.01f, 2000f)]
        public float MaxDistance = 100.0f; // meters

        [SensorParameter]
        [Range(1, 30)]
        public float RotationFrequency = 5.0f; // Hz

        [SensorParameter]
        [Range(18, 6000)] // minmimum is 360/HorizontalAngleLimit
        public int MeasurementsPerRotation = 1500; // for each ray

        [SensorParameter]
        public bool Compensated = true;
        
        public GameObject Top = null;

        [SensorParameter]
        [Range(1, 10)]
        public float PointSize = 2.0f;

        [SensorParameter]
        public Color PointColor = Color.red;

        protected IBridge Bridge;
        protected IWriter<PointCloudData> Writer;
        protected uint SendSequence;

        [NativeDisableContainerSafetyRestriction]
        protected NativeArray<Vector4> Points;

        ComputeBuffer PointCloudBuffer;
        int PointCloudLayer;

        Material PointCloudMaterial;

        private bool updated;
        protected NativeArray<float> SinLatitudeAngles;
        protected NativeArray<float> CosLatitudeAngles;
        private Camera sensorCamera;

        protected Camera SensorCamera
        {
            get
            {
                if (sensorCamera == null)
                    sensorCamera = GetComponentInChildren<Camera>();

                return sensorCamera;
            }
        }

        protected struct ReadRequest
        {
            public TextureSet TextureSet;
            public AsyncGPUReadbackRequest Readback;
            public int Index;
            public int Count;
            public float AngleStart;
            public uint TimeStamp;
            public Vector3 Origin;

            public Matrix4x4 Transform;
            public Matrix4x4 CameraToWorldMatrix;
        }

        protected class TextureSet
        {
            public RTHandle colorTexture;
            public RTHandle depthTexture;

            public void Alloc(int width, int height)
            {
                colorTexture = RTHandles.Alloc(
                    width,
                    height,
                    TextureXR.slices,
                    DepthBits.None,
                    GraphicsFormat.R8G8B8A8_UNorm,
                    dimension: TextureXR.dimension,
                    useDynamicScale: true,
                    name: "Lidar Texture",
                    wrapMode: TextureWrapMode.Clamp);
                
                // TODO: Depth texture can be shared since its not processed, extract it at some point
                depthTexture = RTHandles.Alloc(
                    width,
                    height,
                    TextureXR.slices,
                    DepthBits.Depth32,
                    GraphicsFormat.R32_UInt,
                    dimension: TextureXR.dimension,
                    useDynamicScale: true,
                    name: "Lidar Depth Texture",
                    wrapMode: TextureWrapMode.Clamp);
            }

            public bool IsValid()
            {
                return colorTexture != null &&
                       colorTexture.rt.IsCreated() &&
                       depthTexture != null &&
                       depthTexture.rt.IsCreated();
            }

            public void Release()
            {
                if (colorTexture != null)
                {
                    RTHandles.Release(colorTexture);
                    colorTexture = null;
                }

                if (depthTexture != null)
                {
                    RTHandles.Release(depthTexture);
                    depthTexture = null;
                }
            }
        }

        List<ReadRequest> Active = new List<ReadRequest>();
        List<JobHandle> Jobs = new List<JobHandle>();

        Stack<TextureSet> AvailableRenderTextures = new Stack<TextureSet>();
        Stack<Texture2D> AvailableTextures = new Stack<Texture2D>();

        int CurrentIndex;
        float AngleStart;
        float AngleDelta;

        protected float MaxAngle;
        protected int RenderTextureWidth;
        protected int RenderTextureHeight;
        protected float StartLatitudeAngle;
        protected float EndLatitudeAngle;
        protected float SinStartLongitudeAngle;
        protected float CosStartLongitudeAngle;
        protected float SinDeltaLongitudeAngle;
        protected float CosDeltaLongitudeAngle;

        // Scales between world coordinates and texture coordinates
        protected float XScale;
        protected float YScale;
        
        float IgnoreNewRquests;

        ProfilerMarker UpdateMarker = new ProfilerMarker("Lidar.Update");
        ProfilerMarker VisualizeMarker = new ProfilerMarker("Lidar.Visualzie");
        ProfilerMarker BeginReadMarker = new ProfilerMarker("Lidar.BeginRead");
        protected ProfilerMarker EndReadMarker = new ProfilerMarker("Lidar.EndRead");

        private TextureSet activeTarget;

        public override SensorDistributionType DistributionType => SensorDistributionType.UltraHighLoad;

        public override void OnBridgeSetup(IBridge bridge)
        {
            Bridge = bridge;
            Writer = bridge.AddWriter<PointCloudData>(Topic);
        }

        public void CustomRender(ScriptableRenderContext context, HDCamera hd)
        {
            var camera = hd.camera;

            var cmd = CommandBufferPool.Get();
            // NOTE: Target setting is done in BeginReadRequest through Camera.SetTargetBuffers. Doing it through
            //       the command queue changes output slightly, probably should be debugged eventually (low priority).
            // CoreUtils.SetRenderTarget(cmd, activeTarget.colorTexture, activeTarget.depthTexture);
            hd.SetupGlobalParams(cmd, 0);
            
            ScriptableCullingParameters culling;
            if (camera.TryGetCullingParameters(out culling))
            {
                var cull = context.Cull(ref culling);

                context.SetupCameraProperties(camera);
                CoreUtils.ClearRenderTarget(cmd, ClearFlag.All, Color.clear);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var sorting = new SortingSettings(camera);
                var drawing = new DrawingSettings(new ShaderTagId("SimulatorLidarPass"), sorting);
                var filter = new FilteringSettings(RenderQueueRange.all);

                context.DrawRenderers(cull, ref drawing, ref filter);
            }

            PointCloudManager.RenderLidar(context, cmd, hd, activeTarget.colorTexture, activeTarget.depthTexture);
            CommandBufferPool.Release(cmd);
        }

        public virtual void Init()
        {
            var hd = SensorCamera.GetComponent<HDAdditionalCameraData>();
            hd.hasPersistentHistory = true;
            hd.customRender += CustomRender;
            PointCloudMaterial = new Material(RuntimeSettings.Instance.PointCloudShader);
            PointCloudLayer = LayerMask.NameToLayer("Sensor Effects");

            Reset();
        }

        private void Start()
        {
            Init();
        }

        private float CalculateFovAngle(float latitudeAngle, float logitudeAngle)
        {
            // Calculate a direction (dx, dy, dz) using lat/log angles
            float dy = Mathf.Cos(latitudeAngle * Mathf.Deg2Rad);
            float rProjected = Mathf.Sin(latitudeAngle * Mathf.Deg2Rad);
            float dz = rProjected * Mathf.Sin(logitudeAngle * Mathf.Deg2Rad);
            float dx = rProjected * Mathf.Cos(logitudeAngle * Mathf.Deg2Rad);

            // Project the driection to near plane
            float projectionScale = MinDistance / dz;
            float xx = dx * projectionScale;
            float yy = dy * projectionScale;

            return Mathf.Abs(Mathf.Atan2(yy, MinDistance) * Mathf.Rad2Deg);
        }

        public virtual void Reset()
        {
            Active.ForEach(req =>
            {
                req.Readback.WaitForCompletion();
                req.TextureSet.Release();
            });
            Active.Clear();

            Jobs.ForEach(job => job.Complete());
            Jobs.Clear();

            foreach (var tex in AvailableRenderTextures)
            {
                tex.Release();
            };
            AvailableRenderTextures.Clear();

            foreach (var tex in AvailableTextures)
            {
                Destroy(tex);
            };
            AvailableTextures.Clear();

            if (PointCloudBuffer != null)
            {
                PointCloudBuffer.Release();
                PointCloudBuffer = null;
            }

            if (Points.IsCreated)
            {
                Points.Dispose();
            }

            AngleStart = 0.0f;
            // Assuming center of view frustum is horizontal, find the vertical FOV (of view frustum) that can encompass the tilted Lidar FOV.
            // "MaxAngle" is half of the vertical FOV of view frustum.
            if (VerticalRayAngles.Count == 0)
            {
                MaxAngle = Mathf.Abs(CenterAngle) + FieldOfView / 2.0f;

                StartLatitudeAngle = 90.0f + MaxAngle;
                //If the Lidar is tilted up, ignore lower part of the vertical FOV.
                if (CenterAngle < 0.0f)
                {
                    StartLatitudeAngle -= MaxAngle * 2.0f - FieldOfView;
                }
                EndLatitudeAngle = StartLatitudeAngle - FieldOfView;
            }
            else
            {
                LaserCount = VerticalRayAngles.Count;
                StartLatitudeAngle = 90.0f - VerticalRayAngles.Min();
                EndLatitudeAngle = 90.0f - VerticalRayAngles.Max();
                FieldOfView = StartLatitudeAngle - EndLatitudeAngle;
                MaxAngle = Mathf.Max(StartLatitudeAngle - 90.0f, 90.0f - EndLatitudeAngle);
            }

            float startLongitudeAngle = 90.0f + HorizontalAngleLimit / 2.0f;
            SinStartLongitudeAngle = Mathf.Sin(startLongitudeAngle * Mathf.Deg2Rad);
            CosStartLongitudeAngle = Mathf.Cos(startLongitudeAngle * Mathf.Deg2Rad);

            // The MaxAngle above is the calculated at the center of the view frustum.
            // Because the scan curve for a particular laser ray is a hyperbola (intersection of a conic surface and a vertical plane),
            // the vertical FOV should be enlarged toward left and right ends.
            float startFovAngle = CalculateFovAngle(StartLatitudeAngle, startLongitudeAngle);
            float endFovAngle = CalculateFovAngle(EndLatitudeAngle, startLongitudeAngle);
            MaxAngle = Mathf.Max(MaxAngle, Mathf.Max(startFovAngle, endFovAngle));

            // Calculate sin/cos of latitude angle of each ray.
            if (SinLatitudeAngles.IsCreated)
            {
                SinLatitudeAngles.Dispose();
            }
            if (CosLatitudeAngles.IsCreated)
            {
                CosLatitudeAngles.Dispose();
            }
            SinLatitudeAngles = new NativeArray<float>(LaserCount, Allocator.Persistent);
            CosLatitudeAngles = new NativeArray<float>(LaserCount, Allocator.Persistent);


            int totalCount = LaserCount * MeasurementsPerRotation;
            PointCloudBuffer = new ComputeBuffer(totalCount, UnsafeUtility.SizeOf<Vector4>());
            PointCloudMaterial?.SetBuffer("_PointCloud", PointCloudBuffer);

            Points = new NativeArray<Vector4>(totalCount, Allocator.Persistent);

            CurrentLaserCount = LaserCount;
            CurrentMeasurementsPerRotation = MeasurementsPerRotation;
            CurrentFieldOfView = FieldOfView;
            CurrentVerticalRayAngles = new List<float>(VerticalRayAngles);
            CurrentCenterAngle = CenterAngle;
            CurrentMinDistance = MinDistance;
            CurrentMaxDistance = MaxDistance;

            IgnoreNewRquests = 0;
        }

        void OnDisable()
        {
            Active.ForEach(req =>
            {
                req.Readback.WaitForCompletion();
                req.TextureSet.Release();
            });
            Active.Clear();

            Jobs.ForEach(job => job.Complete());
            Jobs.Clear();
        }

        public virtual void Update()
        {
            if (LaserCount != CurrentLaserCount ||
                MeasurementsPerRotation != CurrentMeasurementsPerRotation ||
                FieldOfView != CurrentFieldOfView ||
                CenterAngle != CurrentCenterAngle ||
                MinDistance != CurrentMinDistance ||
                MaxDistance != CurrentMaxDistance ||
                !Enumerable.SequenceEqual(VerticalRayAngles, CurrentVerticalRayAngles))
            {
                if (MinDistance > 0 && MaxDistance > 0 && LaserCount > 0 && MeasurementsPerRotation >= (360.0f / HorizontalAngleLimit))
                {
                    Reset();
                }
            }

            UpdateMarker.Begin();

            updated = false;
            while (Jobs.Count > 0 && Jobs[0].IsCompleted)
            {
                updated = true;
                Jobs.RemoveAt(0);
            }

            bool jobsIssued = false;
            while (Active.Count > 0)
            {
                var req = Active[0];
                if (!req.TextureSet.IsValid())
                {
                    // lost render texture, probably due to Unity window resize or smth
                    req.Readback.WaitForCompletion();
                    req.TextureSet.Release();
                }
                else if (req.Readback.done)
                {
                    if (req.Readback.hasError)
                    {
                        Debug.Log("Failed to read GPU texture");
                        req.TextureSet.Release();
                        IgnoreNewRquests = 1.0f;
                    }
                    else
                    {
                        jobsIssued = true;
                        var job = EndReadRequest(req, req.Readback.GetData<byte>());
                        Jobs.Add(job);
                        AvailableRenderTextures.Push(req.TextureSet);

                        if (req.Index + req.Count >= CurrentMeasurementsPerRotation)
                        {
                            SendMessage();
                        }
                    }
                }
                else
                {
                    break;
                }

                Active.RemoveAt(0);
            }

            if (jobsIssued)
            {
                JobHandle.ScheduleBatchedJobs();
            }

            if (IgnoreNewRquests > 0)
            {
                IgnoreNewRquests -= Time.unscaledDeltaTime;
            }
            else
            {
                float minAngle = 360.0f / CurrentMeasurementsPerRotation;

                AngleDelta += Time.deltaTime * 360.0f * RotationFrequency;
                int count = (int)(HorizontalAngleLimit / minAngle);

                while (AngleDelta >= HorizontalAngleLimit)
                {
                    float angle = AngleStart + HorizontalAngleLimit / 2.0f;
                    var rotation = Quaternion.AngleAxis(angle, Vector3.up);
                    SensorCamera.transform.localRotation = rotation;
                    if (Top != null)
                    {
                        Top.transform.localRotation = rotation;
                    }

                    var req = new ReadRequest();
                    if (BeginReadRequest(count, ref req))
                    {
                        req.Readback = AsyncGPUReadback.Request((RenderTexture)req.TextureSet.colorTexture, 0);
                        req.AngleStart = AngleStart;

                        DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds((long)(SimulatorManager.Instance.CurrentTime * 1000.0)).UtcDateTime;
                        DateTime startHour = new DateTime(dt.Year, dt.Month,
                            dt.Day, dt.Hour, 0, 0);
                        TimeSpan timeOverHour = dt - startHour;
                        req.TimeStamp = (uint)(timeOverHour.Ticks / (TimeSpan.TicksPerMillisecond) * 1000);

                        Active.Add(req);
                    }

                    AngleDelta -= HorizontalAngleLimit;
                    AngleStart += HorizontalAngleLimit;

                    if (AngleStart >= 360.0f)
                    {
                        AngleStart -= 360.0f;
                    }
                }
            }

            UpdateMarker.End();
        }

        public virtual void OnDestroy()
        {
            Active.ForEach(req =>
            {
                req.Readback.WaitForCompletion();
                req.TextureSet.Release();
            });

            Jobs.ForEach(job => job.Complete());

            foreach (var tex in AvailableRenderTextures)
            {
                tex.Release();
            }
            foreach (var tex in AvailableTextures)
            {
                DestroyImmediate(tex);
            }

            PointCloudBuffer?.Release();

            if (Points.IsCreated)
            {
                Points.Dispose();
            }

            if (PointCloudMaterial != null)
            {
                DestroyImmediate(PointCloudMaterial);
            }

            if (SinLatitudeAngles.IsCreated)
            {
                SinLatitudeAngles.Dispose();
            }
            if (CosLatitudeAngles.IsCreated)
            {
                CosLatitudeAngles.Dispose();
            }
        }

        bool BeginReadRequest(int count, ref ReadRequest req)
        {
            if (count == 0)
            {
                return false;
            }

            BeginReadMarker.Begin();

            TextureSet TextureSet = null;
            if (AvailableRenderTextures.Count != 0)
                TextureSet = AvailableRenderTextures.Pop();

            if (TextureSet == null)
            {
                TextureSet = new TextureSet();
                TextureSet.Alloc(RenderTextureWidth, RenderTextureHeight);
            }
            else if (!TextureSet.IsValid())
            {
                TextureSet.Release();
                TextureSet.Alloc(RenderTextureWidth, RenderTextureHeight);
            }

            activeTarget = TextureSet;
            SensorCamera.SetTargetBuffers(
                activeTarget.colorTexture.rt.colorBuffer,
                activeTarget.depthTexture.rt.depthBuffer);
            SensorCamera.Render();

            req = new ReadRequest()
            {
                TextureSet = TextureSet,
                Index = CurrentIndex,
                Count = count,
                Origin = SensorCamera.transform.position,

                CameraToWorldMatrix = SensorCamera.cameraToWorldMatrix,
            };

            if (!Compensated)
            {
                req.Transform = transform.worldToLocalMatrix;
            }

            BeginReadMarker.End();

            CurrentIndex = (CurrentIndex + count) % CurrentMeasurementsPerRotation;

            return true;
        }

        protected abstract JobHandle EndReadRequest(ReadRequest req, NativeArray<byte> textureData);

        protected abstract void SendMessage();

        public NativeArray<Vector4> Capture()
        {
            Debug.Assert(Compensated); // points should be in world-space
            int rotationCount = Mathf.CeilToInt(360.0f / HorizontalAngleLimit);

            float minAngle = 360.0f / CurrentMeasurementsPerRotation;
            int count = (int)(HorizontalAngleLimit / minAngle);

            float angle = HorizontalAngleLimit / 2.0f;

            var jobs = new NativeArray<JobHandle>(rotationCount, Allocator.Persistent);
#if ASYNC
            var active = new ReadRequest[rotationCount];

            try
            {
                for (int i = 0; i < rotationCount; i++)
                {
                    var rotation = Quaternion.AngleAxis(angle, Vector3.up);
                    Camera.transform.localRotation = rotation;

                    if (BeginReadRequest(count, angle, HorizontalAngleLimit, ref active[i]))
                    {
                        active[i].Readback = AsyncGPUReadback.Request(active[i].RenderTexture, 0);
                    }

                    angle += HorizontalAngleLimit;
                    if (angle >= 360.0f)
                    {
                        angle -= 360.0f;
                    }
                }

                for (int i = 0; i < rotationCount; i++)
                {
                    active[i].Readback.WaitForCompletion();
                    jobs[i] = EndReadRequest(active[i], active[i].Readback.GetData<byte>());
                }

                JobHandle.CompleteAll(jobs);
            }
            finally
            {
                Array.ForEach(active, req => AvailableRenderTextures.Push(req.RenderTexture));
                jobs.Dispose();
            }
#else
            var textures = new Texture2D[rotationCount];

            var rt = RenderTexture.active;
            try
            {
                for (int i = 0; i < rotationCount; i++)
                {
                    var rotation = Quaternion.AngleAxis(angle, Vector3.up);
                    SensorCamera.transform.localRotation = rotation;

                    var req = new ReadRequest();
                    if (BeginReadRequest(count, ref req))
                    {
                        RenderTexture.active = req.TextureSet.colorTexture.rt;
                        Texture2D texture;
                        if (AvailableTextures.Count > 0)
                        {
                            texture = AvailableTextures.Pop();
                        }
                        else
                        {
                            texture = new Texture2D(RenderTextureWidth, RenderTextureHeight, TextureFormat.RGBA32, false, true);
                        }
                        texture.ReadPixels(new Rect(0, 0, RenderTextureWidth, RenderTextureHeight), 0, 0);
                        textures[i] = texture;
                        jobs[i] = EndReadRequest(req, texture.GetRawTextureData<byte>());

                        AvailableRenderTextures.Push(req.TextureSet);
                    }

                    angle += HorizontalAngleLimit;
                    if (angle >= 360.0f)
                    {
                        angle -= 360.0f;
                    }
                }

                JobHandle.CompleteAll(jobs);
            }
            finally
            {
                RenderTexture.active = rt;
                Array.ForEach(textures, AvailableTextures.Push);
                jobs.Dispose();
            }
#endif

            return Points;
        }

        public bool Save(string path)
        {
            int rotationCount = Mathf.CeilToInt(360.0f / HorizontalAngleLimit);

            float minAngle = 360.0f / CurrentMeasurementsPerRotation;
            int count = (int)(HorizontalAngleLimit / minAngle);

            float angle = HorizontalAngleLimit / 2.0f;

            var jobs = new NativeArray<JobHandle>(rotationCount, Allocator.Persistent);

            var active = new ReadRequest[rotationCount];

            try
            {
                for (int i = 0; i < rotationCount; i++)
                {
                    var rotation = Quaternion.AngleAxis(angle, Vector3.up);
                    SensorCamera.transform.localRotation = rotation;

                    if (BeginReadRequest(count, ref active[i]))
                    {
                        active[i].Readback = AsyncGPUReadback.Request(active[i].TextureSet.colorTexture, 0);
                    }

                    angle += HorizontalAngleLimit;
                    if (angle >= 360.0f)
                    {
                        angle -= 360.0f;
                    }
                }

                for (int i = 0; i < rotationCount; i++)
                {
                    active[i].Readback.WaitForCompletion();
                    jobs[i] = EndReadRequest(active[i], active[i].Readback.GetData<byte>());
                }

                JobHandle.CompleteAll(jobs);
            }
            finally
            {
                Array.ForEach(active, req => AvailableRenderTextures.Push(req.TextureSet));
                jobs.Dispose();
            }

            var worldToLocal = LidarTransform;
            if (Compensated)
            {
                worldToLocal = worldToLocal * transform.worldToLocalMatrix;
            }

            try
            {
                using (var writer = new PcdWriter(path))
                {
                    for (int p = 0; p < Points.Length; p++)
                    {
                        var point = Points[p];
                        if (point != Vector4.zero)
                        {
                            writer.Write(worldToLocal.MultiplyPoint3x4(point), point.w);
                        }
                    };
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return false;
            }
        }

        public override void OnVisualize(Visualizer visualizer)
        {
            VisualizeMarker.Begin();
            if (updated)
            {
                PointCloudBuffer.SetData(Points);
            }

            var lidarToWorld = Compensated ? Matrix4x4.identity : transform.localToWorldMatrix;
            PointCloudMaterial.SetMatrix("_LocalToWorld", lidarToWorld);
            PointCloudMaterial.SetFloat("_Size", PointSize * Utility.GetDpiScale());
            PointCloudMaterial.SetColor("_Color", PointColor);
            Graphics.DrawProcedural(PointCloudMaterial, new Bounds(transform.position, MaxDistance * Vector3.one), MeshTopology.Points, PointCloudBuffer.count, layer: LayerMask.NameToLayer("Sensor"));

            VisualizeMarker.End();
        }

        public override void OnVisualizeToggle(bool state)
        {
            //
        }

        public override bool CheckVisible(Bounds bounds)
        {
            var activeCameraPlanes = GeometryUtility.CalculateFrustumPlanes(SensorCamera);
            return GeometryUtility.TestPlanesAABB(activeCameraPlanes, bounds);
        }
    }
}
