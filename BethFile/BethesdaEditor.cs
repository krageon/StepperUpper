﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using static BethFile.B4S;

namespace BethFile
{
    public static class BethesdaEditor
    {
        public static void SortRecords(BethesdaGroup group)
        {
            byte[] newData = new byte[group.DataSize];
            List<BethesdaRecord> records = new List<BethesdaRecord>();
            BethesdaGroupReader reader = new BethesdaGroupReader(group);
            BethesdaGroupReaderState state = reader.Read();
            while (state != BethesdaGroupReaderState.EndOfContent)
            {
                if (state != BethesdaGroupReaderState.Record)
                {
                    throw new NotSupportedException("Only groups with strictly records are supported.");
                }

                records.Add(reader.CurrentRecord);
                state = reader.Read();
            }

            uint pos = 0;
            foreach (var record in records.OrderBy(rec => rec.Id))
            {
                UBuffer.BlockCopy(record.RawData, 0, newData, pos, record.RawData.Count);
                pos += record.RawData.Count;
            }

            UBuffer.BlockCopy(newData, 0, group.PayloadData, 0, group.DataSize);
        }

        public static void MoveSubgroupToBottom(BethesdaGroup group, BethesdaGroupType subgroupType, uint subgroupLabel)
        {
            BethesdaGroup subgroup = default(BethesdaGroup);
            BethesdaGroupReader reader = new BethesdaGroupReader(group);
            BethesdaGroupReaderState state = reader.Read();
            while (state != BethesdaGroupReaderState.EndOfContent)
            {
                if (state != BethesdaGroupReaderState.Subgroup)
                {
                    state = reader.Read();
                    continue;
                }

                subgroup = reader.CurrentSubgroup;
                if (subgroup.GroupType == subgroupType && subgroup.Label == subgroupLabel)
                {
                    break;
                }

                state = reader.Read();
            }

            if (state == BethesdaGroupReaderState.EndOfContent)
            {
                throw new InvalidOperationException("Subgroup not found.");
            }

            if (reader.Read() == BethesdaGroupReaderState.EndOfContent)
            {
                Debug.Fail("Subgroup is already at the end");
                return;
            }

            UArraySegment<byte> grpRawData = group.RawData;
            UArraySegment<byte> subRawData = subgroup.RawData;
            byte[] subgroupData = new byte[subRawData.Count];
            UBuffer.BlockCopy(subRawData, 0, subgroupData, 0, subRawData.Count);
            UBuffer.BlockCopy(grpRawData.Array, subRawData.Offset + subRawData.Count, grpRawData.Array, subRawData.Offset, grpRawData.Count - (subRawData.Offset - grpRawData.Offset) - subRawData.Count);
            UBuffer.BlockCopy(subgroupData, 0, grpRawData.Array, grpRawData.Offset + (grpRawData.Count - subRawData.Count), subRawData.Count);
        }

        // this is still very much a work-in-progress... I almost want to comment it out entirely.
        public static BethesdaRecord UndeleteAndDisableReference(BethesdaRecord record, BethesdaRecord template)
        {
            if (record.Type != REFR)
            {
                throw new ArgumentException("Must be a REFR.", nameof(record));
            }

            byte[] rawData = new byte[template.Payload.Count + 68];
            UBuffer.BlockCopy(record.RawData, 0, rawData, 0, 24);
            record = new BethesdaRecord(rawData);
            record.DataSize = template.DataSize;
            UBuffer.BlockCopy(template.Payload, 0, record.Payload, 0, template.Payload.Count);

            record.Flags = (record.Flags & ~BethesdaRecordFlags.Deleted) | BethesdaRecordFlags.InitiallyDisabled;

            int handled = 0;
            foreach (var field in record.Fields)
            {
                switch (field.Type)
                {
                    case _DATA:
                        if ((handled & 2) != 0)
                        {
                            throw new InvalidDataException("DATA shows up twice.");
                        }

                        handled |= 2;
                        UDR_DATA(field);
                        break;

                    case _XESP:
                        if ((handled & 1) != 0)
                        {
                            throw new InvalidDataException("XESP shows up twice.");
                        }

                        UDR_XESP(field);
                        handled |= 1;
                        break;
                }

                if (handled == 3)
                {
                    break;
                }
            }

            if ((handled & 2) == 0)
            {
                record.DataSize += 30;
                BethesdaField field = new BethesdaField(new UArraySegment<byte>(rawData, record.DataSize - 30, 30));
                UBitConverter.SetUInt32(field.Start, DATA);
                UBitConverter.SetUInt16(field.Start + 4, 24);
                UDR_DATA(field);
            }

            if ((handled & 1) == 0)
            {
                record.DataSize += 14;
                BethesdaField field = new BethesdaField(new UArraySegment<byte>(rawData, record.DataSize - 14, 14));
                UBitConverter.SetUInt32(field.Start, XESP);
                UBitConverter.SetUInt16(field.Start + 4, 8);
                UDR_XESP(field);
            }

            return record;
        }

        private static void UDR_XESP(BethesdaField field)
        {
            UBitConverter.SetUInt32(field.PayloadStart, 0x14);
            UBitConverter.SetUInt32(field.PayloadStart + 4, 0x01);
        }

        private static void UDR_DATA(BethesdaField field)
        {
            ////System.BitConverter.ToUInt32(System.BitConverter.GetBytes((float)-30000), 0)
            UBitConverter.SetUInt32(field.PayloadStart + 8, 3337248768);
        }
    }
}