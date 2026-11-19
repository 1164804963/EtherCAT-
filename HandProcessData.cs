using System;
using System.Collections.Generic;
using System.Linq;

namespace EtherCatBench
{
    struct HandTarget
    {
        public int Axis;
        public ushort Angle, Torque, Speed;
    }

    sealed class HandPdoLayout
    {
        public const int Axes = 6;
        public const int TemperatureSamples = 64;
        public const int TouchGroups = 5;
        public const int TouchSamples = 48;

        readonly int[] targets = Offsets(3 * Axes);
        readonly int[] feedback = Offsets(2 * Axes);
        readonly int[] temperatures = Offsets(TemperatureSamples);
        readonly int[][] touches = Enumerable.Range(0, TouchGroups).Select(i => Offsets(TouchSamples)).ToArray();

        static int[] Offsets(int count)
        {
            return Enumerable.Repeat(-1, count).ToArray();
        }

        public static HandPdoLayout FromMapping(IEnumerable<PdoField> mapping, bool checkTypes = false)
        {
            var layout = new HandPdoLayout();
            if (mapping == null) return layout;
            int rxOffset = 0, txOffset = 0;
            foreach (var field in mapping)
            {
                if (field.Direction == "RxPDO")
                {
                    if (field.Index == 0x7000)
                        Put(layout.targets, field, rxOffset, 16, "UINT", checkTypes);
                    rxOffset += (int)field.Bits;
                }
                else if (field.Direction == "TxPDO")
                {
                    if (field.Index == 0x6000)
                        Put(layout.feedback, field, txOffset, 16, "INT", checkTypes);
                    else if (field.Index == 0x6001)
                        Put(layout.temperatures, field, txOffset, 8, "USINT", checkTypes);
                    else if (field.Index >= 0x6002 && field.Index < 0x6002 + TouchGroups)
                        Put(layout.touches[field.Index - 0x6002], field, txOffset, 8, "USINT", checkTypes);
                    txOffset += (int)field.Bits;
                }
            }
            return layout;
        }

        static void Put(int[] offsets, PdoField field, int bitOffset, uint width, string type, bool checkTypes)
        {
            if (field.Sub == 0 || field.Sub > offsets.Length) return;
            int sub = (int)field.Sub - 1;
            offsets[sub] = offsets[sub] == -1 && field.Bits == width &&
                (!checkTypes || string.Equals(field.Type, type, StringComparison.OrdinalIgnoreCase))
                ? bitOffset : -2;
        }

        public bool Complete
        {
            get {
                return targets.All(x => x >= 0) && feedback.All(x => x >= 0) &&
                    temperatures.All(x => x >= 0) && touches.All(group => group.All(x => x >= 0));
            }
        }

        public ushort? ReadSent(byte[] data, int axis, int value)
        {
            byte[] bytes = FieldValue.Extract(data, targets[value * Axes + axis], 16);
            return bytes == null ? (ushort?)null : BitConverter.ToUInt16(bytes, 0);
        }

        public short? ReadFeedback(byte[] data, int axis, int value)
        {
            byte[] bytes = FieldValue.Extract(data, feedback[value * Axes + axis], 16);
            return bytes == null ? (short?)null : BitConverter.ToInt16(bytes, 0);
        }

        public byte[] ReadTemperatures(byte[] data) { return ReadSamples(data, temperatures); }
        public byte[] ReadTouch(byte[] data, int group) { return ReadSamples(data, touches[group]); }

        static byte[] ReadSamples(byte[] data, int[] offsets)
        {
            if (data == null) return null;
            var samples = new byte[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
            {
                byte[] value = FieldValue.Extract(data, offsets[i], 8);
                if (value == null) return null;
                samples[i] = value[0];
            }
            return samples;
        }

        public byte[] StageTargets(byte[] buffer, HandTarget[] commands)
        {
            if (!Complete || buffer == null)
                throw new InvalidOperationException("灵巧手在线 PDO 映射不完整。");
            if (commands == null || commands.Length == 0 || commands.Length > Axes)
                throw new ArgumentException("请选择要应用的轴。");

            var staged = (byte[])buffer.Clone();
            var seen = new HashSet<int>();
            foreach (var command in commands)
            {
                if (command.Axis < 0 || command.Axis >= Axes || !seen.Add(command.Axis))
                    throw new ArgumentException("目标中包含无效或重复的轴。");
                FieldValue.Insert(staged, targets[command.Axis], 16, BitConverter.GetBytes(command.Angle));
                FieldValue.Insert(staged, targets[Axes + command.Axis], 16, BitConverter.GetBytes(command.Torque));
                FieldValue.Insert(staged, targets[Axes * 2 + command.Axis], 16, BitConverter.GetBytes(command.Speed));
            }
            return staged;
        }
    }
}
