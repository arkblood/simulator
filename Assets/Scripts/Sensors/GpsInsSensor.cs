/**
 * Copyright (c) 2018 LG Electronics, Inc.
 *
 * This software contains code licensed as described in LICENSE.
 *
 */

using Simulator.Bridge;
using Simulator.Bridge.Data;
using Simulator.Utilities;
using UnityEngine;

namespace Simulator.Sensors
{
    [SensorType("GPS-INS Status", new[] { typeof(GpsInsData) })]
    public class GpsInsSensor : SensorBase
    {
        [SensorParameter]
        public float Frequency = 12.5f;

        float NextSend;
        uint SendSequence;

        IBridge Bridge;
        IWriter<GpsInsData> Writer;

        public override void OnBridgeSetup(IBridge bridge)
        {
            Bridge = bridge;
            Writer = Bridge.AddWriter<GpsInsData>(Topic);
        }

        public void Start()
        {
            NextSend = Time.time + 1.0f / Frequency;
        }

        void Update()
        {
            if (Bridge == null || Bridge.Status != Status.Connected)
            {
                return;
            }

            if (Time.time < NextSend)
            {
                return;
            }
            NextSend = Time.time + 1.0f / Frequency;

            Writer.Write(new GpsInsData()
            {
                Status = 3,
                PositionType = 56,
            });
        }
    }
}