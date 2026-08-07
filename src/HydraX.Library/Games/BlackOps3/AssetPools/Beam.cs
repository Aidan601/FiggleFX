using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HydraX.Library
{
    public partial class BlackOps3
    {
        /// <summary>
        /// Black Ops 3 beam definitions (pool "beam", index 101) — the assets
        /// beamSource/beamTarget fx emitters reference by name. Standalone
        /// GDT export shared with FiggleFX (replaces the stock gdt.db-machinery
        /// path): the BeamOffsets table below is the stock decoder's, values
        /// are written directly as a shipping-format beam.gdf GDT to
        /// exported_files/&lt;game&gt;/beams/. Validated 159/159 keys exact
        /// against zod_beast_grapple_beam's shipping GDT (2026-08-07).
        /// </summary>
        private class Beam : IAssetPool
        {
            #region Tables
            /// <summary>
            /// Attachment Properties and Struct Offsets
            /// </summary>
            private static readonly Tuple<string, int, int>[] BeamOffsets =
            {
                new Tuple<string, int, int>("material",                                                      0x8, 0x33),
                new Tuple<string, int, int>("maxLength",                                                     0x24, 0x8),
                new Tuple<string, int, int>("useVirtualTarget",                                              0x2ab, 0x6),
                new Tuple<string, int, int>("virtualTargetDistance",                                         0x28, 0x8),
                new Tuple<string, int, int>("virtualTargetMass",                                             0x2c, 0x8),
                new Tuple<string, int, int>("beamAffectedByGravity",                                         0x2ac, 0x6),
                new Tuple<string, int, int>("beamInitialSpeed",                                              0x30, 0x8),
                new Tuple<string, int, int>("beamGravity",                                                   0x34, 0x8),
                new Tuple<string, int, int>("numSegments",                                                   0x18, 0x4),
                new Tuple<string, int, int>("curveType",                                                     0x1c, 0x35),
                new Tuple<string, int, int>("msecAnimLoopTime",                                              0x20, 0x4),
                new Tuple<string, int, int>("launchSpeed",                                                   0x38, 0x8),
                new Tuple<string, int, int>("startWidth",                                                    0x3c, 0x8),
                new Tuple<string, int, int>("endWidth",                                                      0x40, 0x8),
                new Tuple<string, int, int>("leadoutDistance",                                               0x48, 0x8),
                new Tuple<string, int, int>("breakAngle",                                                    0x4c, 0x8),
                new Tuple<string, int, int>("leadinDistance",                                                0x50, 0x8),
                new Tuple<string, int, int>("retractSpeed",                                                  0x54, 0x8),
                new Tuple<string, int, int>("reverseRetract",                                                0x2ad, 0x6),
                new Tuple<string, int, int>("beamEffectDistance",                                            0x58, 0x8),
                new Tuple<string, int, int>("beamEffectSpeed",                                               0x5c, 0x8),
                new Tuple<string, int, int>("originEffect",                                                  0x270, 0xa),
                new Tuple<string, int, int>("targetEffect",                                                  0x278, 0xa),
                new Tuple<string, int, int>("deathEffect",                                                   0x280, 0xa),
                new Tuple<string, int, int>("beamEffect",                                                    0x288, 0xa),
                new Tuple<string, int, int>("blockedEffect",                                                 0x290, 0xa),
                new Tuple<string, int, int>("blockedEffectSound",                                            0x298, 0x0),
                new Tuple<string, int, int>("unblockedEffectSound",                                          0x2a0, 0x0),
                new Tuple<string, int, int>("dieIfTooLong",                                                  0x2a8, 0x6),
                new Tuple<string, int, int>("collisionMode",                                                 0x74, 0x39),
                new Tuple<string, int, int>("collisionMask",                                                 0x78, 0x3a),
                new Tuple<string, int, int>("dieIfBlocked",                                                  0x2a9, 0x6),
                new Tuple<string, int, int>("perpendicularMaterial",                                         0x10, 0x33),
                new Tuple<string, int, int>("drawPerpendicularCard",                                         0x2aa, 0x6),
                new Tuple<string, int, int>("uvMode",                                                        0x60, 0x34),
                new Tuple<string, int, int>("beamShape",                                                     0x64, 0x38),
                new Tuple<string, int, int>("beamShapeRotation",                                             0x68, 0x8),
                new Tuple<string, int, int>("beamShapeHorizontalScale",                                      0x6c, 0x8),
                new Tuple<string, int, int>("beamShapeVerticalScale",                                        0x70, 0x8),
                new Tuple<string, int, int>("textureRepeatLength",                                           0x44, 0x8),
                new Tuple<string, int, int>("startColor",                                                    0x7c, 0x36),
                new Tuple<string, int, int>("endColor",                                                      0x8c, 0x36),
                new Tuple<string, int, int>("sineAmplitudeX",                                                0x9c, 0x8),
                new Tuple<string, int, int>("sineAmplitudeY",                                                0xa0, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientStart",                                    0xa4, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientP1",                                       0xa8, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientP2",                                       0xac, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientEnd",                                      0xb0, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientStartValue",                               0xb4, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientP1Value",                                  0xb8, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientP2Value",                                  0xbc, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientEndValue",                                 0xc0, 0x8),
                new Tuple<string, int, int>("sineAmplitudeGradientPower",                                    0xc4, 0x8),
                new Tuple<string, int, int>("sineFrequencyX",                                                0xc8, 0x8),
                new Tuple<string, int, int>("sineFrequencyY",                                                0xcc, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientStart",                                    0xd0, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientP1",                                       0xd4, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientP2",                                       0xd8, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientEnd",                                      0xdc, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientStartValue",                               0xe0, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientP1Value",                                  0xe4, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientP2Value",                                  0xe8, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientEndValue",                                 0xec, 0x8),
                new Tuple<string, int, int>("sineFrequencyGradientPower",                                    0xf0, 0x8),
                new Tuple<string, int, int>("sineSpeedX",                                                    0xf4, 0x8),
                new Tuple<string, int, int>("sineSpeedY",                                                    0xf8, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeX",                                            0xfc, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeY",                                            0x100, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientStart",                                0x104, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientP1",                                   0x108, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientP2",                                   0x10c, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientEnd",                                  0x110, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientStartValue",                           0x114, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientP1Value",                              0x118, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientP2Value",                              0x11c, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientEndValue",                             0x120, 0x8),
                new Tuple<string, int, int>("sawtoothAmplitudeGradientPower",                                0x124, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyX",                                            0x128, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyY",                                            0x12c, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientStart",                                0x130, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientP1",                                   0x134, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientP2",                                   0x138, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientEnd",                                  0x13c, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientStartValue",                           0x140, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientP1Value",                              0x144, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientP2Value",                              0x148, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientEndValue",                             0x14c, 0x8),
                new Tuple<string, int, int>("sawtoothFrequencyGradientPower",                                0x150, 0x8),
                new Tuple<string, int, int>("sawtoothHarmonics",                                             0x154, 0x4),
                new Tuple<string, int, int>("sawtoothSpeedX",                                                0x158, 0x8),
                new Tuple<string, int, int>("sawtoothSpeedY",                                                0x15c, 0x8),
                new Tuple<string, int, int>("squareAmplitudeX",                                              0x160, 0x8),
                new Tuple<string, int, int>("squareAmplitudeY",                                              0x164, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientStart",                                  0x168, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientP1",                                     0x16c, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientP2",                                     0x170, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientEnd",                                    0x174, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientStartValue",                             0x178, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientP1Value",                                0x17c, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientP2Value",                                0x180, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientEndValue",                               0x184, 0x8),
                new Tuple<string, int, int>("squareAmplitudeGradientPower",                                  0x188, 0x8),
                new Tuple<string, int, int>("squareFrequencyX",                                              0x18c, 0x8),
                new Tuple<string, int, int>("squareFrequencyY",                                              0x190, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientStart",                                  0x194, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientP1",                                     0x198, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientP2",                                     0x19c, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientEnd",                                    0x1a0, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientStartValue",                             0x1a4, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientP1Value",                                0x1a8, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientP2Value",                                0x1ac, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientEndValue",                               0x1b0, 0x8),
                new Tuple<string, int, int>("squareFrequencyGradientPower",                                  0x1b4, 0x8),
                new Tuple<string, int, int>("squareHarmonics",                                               0x1b8, 0x4),
                new Tuple<string, int, int>("squareSpeedX",                                                  0x1bc, 0x8),
                new Tuple<string, int, int>("squareSpeedY",                                                  0x1c0, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeX",                                            0x1c4, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeY",                                            0x1c8, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientStart",                                0x1cc, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientP1",                                   0x1d0, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientP2",                                   0x1d4, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientEnd",                                  0x1d8, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientStartValue",                           0x1dc, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientP1Value",                              0x1e0, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientP2Value",                              0x1e4, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientEndValue",                             0x1e8, 0x8),
                new Tuple<string, int, int>("triangleAmplitudeGradientPower",                                0x1ec, 0x8),
                new Tuple<string, int, int>("triangleFrequencyX",                                            0x1f0, 0x8),
                new Tuple<string, int, int>("triangleFrequencyY",                                            0x1f4, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientStart",                                0x1f8, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientP1",                                   0x1fc, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientP2",                                   0x200, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientEnd",                                  0x204, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientStartValue",                           0x208, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientP1Value",                              0x20c, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientP2Value",                              0x210, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientEndValue",                             0x214, 0x8),
                new Tuple<string, int, int>("triangleFrequencyGradientPower",                                0x218, 0x8),
                new Tuple<string, int, int>("triangleHarmonics",                                             0x21c, 0x4),
                new Tuple<string, int, int>("triangleSpeedX",                                                0x220, 0x8),
                new Tuple<string, int, int>("triangleSpeedY",                                                0x224, 0x8),
                new Tuple<string, int, int>("sineWeightStart",                                               0x228, 0x8),
                new Tuple<string, int, int>("sawtoothWeightStart",                                           0x22c, 0x8),
                new Tuple<string, int, int>("squareWeightStart",                                             0x230, 0x8),
                new Tuple<string, int, int>("triangleWeightStart",                                           0x234, 0x8),
                new Tuple<string, int, int>("sineWeightMid",                                                 0x238, 0x8),
                new Tuple<string, int, int>("sawtoothWeightMid",                                             0x23c, 0x8),
                new Tuple<string, int, int>("squareWeightMid",                                               0x240, 0x8),
                new Tuple<string, int, int>("triangleWeightMid",                                             0x244, 0x8),
                new Tuple<string, int, int>("sineWeightEnd",                                                 0x248, 0x8),
                new Tuple<string, int, int>("sawtoothWeightEnd",                                             0x24c, 0x8),
                new Tuple<string, int, int>("squareWeightEnd",                                               0x250, 0x8),
                new Tuple<string, int, int>("triangleWeightEnd",                                             0x254, 0x8),
                new Tuple<string, int, int>("slackStartTimeMsec",                                            0x258, 0x4),
                new Tuple<string, int, int>("slackEaseInTimeMsec",                                           0x25c, 0x4),
                new Tuple<string, int, int>("slackDurationMsec",                                             0x260, 0x4),
                new Tuple<string, int, int>("slackEaseOutTimeMsec",                                          0x264, 0x4),
                new Tuple<string, int, int>("slackLoopMode",                                                 0x268, 0x37),
                new Tuple<string, int, int>("slackMinSlack",                                                 0x26c, 0x8),
            };

            /// <summary>
            /// Beam UV Modes
            /// </summary>
            private static readonly string[] BeamUVModes =
            {
                "horizontal",
                "repeating horizontal",
                "vertical",
                "repeating vertical"
            };

            /// <summary>
            /// Beam Curve Types
            /// </summary>
            private static readonly string[] BeamCurveTypes =
            {
                "bspline",
                "smooth",
                "rounded"
            };

            /// <summary>
            /// Beam Slack Loop Modes
            /// </summary>
            private static readonly string[] BeamSlackLoopModes =
            {
                "Loop Slack",
                "Keep Slack",
                "One Slack Cycle Only",
                "Stay Slack Until Attached",
            };

            /// <summary>
            /// Beam Shapes
            /// </summary>
            private static readonly string[] BeamShapes =
            {
                "cross",
                "star",
                "ribbon",
                "triangle",
                "square",
                "pentagon",
                "hexagon",
                "heptagon",
                "octagon"
            };

            /// <summary>
            /// Beam Collision Modes
            /// </summary>
            private static readonly string[] BeamCollisionModes =
            {
                "none",
                "nodes",
                "verts"
            };

            /// <summary>
            /// Beam Collision Masks
            /// </summary>
            private static readonly Dictionary<uint, string> BeamCollisionMasks = new Dictionary<uint, string>()
            {
                { 0x280E813,         "shot" },
                { 0x280E833,         "shot and water" } ,
                { 0x806813,          "shot and not body" },
                { 0xFFFFFFFF,        "all" },
                { 0x811,             "solid" },
                { 0x2818011,         "player solid" },
                { 0x10811,           "dead solid" },
                { 0x20,              "water" },
                { 0x801,             "opaque" },
                { 0x2009841,         "can shoot" },
                { 0x806C31,          "client effects" },
                { 0x831,             "ground trace" },
                { 0x280B001,         "cross hair trace" },
                { 0x802013,          "dynent client" },
                { 0x280A893,         "missile shot" },
                { 0x211,             "vehicle" }
            };
            #endregion

            public int AssetSize { get; set; }
            public int AssetCount { get; set; }
            public long StartAddress { get; set; }
            public long EndAddress { get { return StartAddress + (AssetCount * AssetSize); } set => throw new NotImplementedException(); }
            public string Name => "beam";
            public int Index => (int)AssetPool.beam;

            /// <summary>
            /// Loads Assets from this Asset Pool
            /// </summary>
            public List<Asset> Load(HydraInstance instance)
            {
                var results = new List<Asset>();

                var poolInfo = instance.Reader.ReadStruct<AssetPoolInfo>(instance.Game.AssetPoolsAddress + (Index * 0x20));

                StartAddress = poolInfo.PoolPointer;
                AssetSize = poolInfo.AssetSize;
                AssetCount = poolInfo.PoolSize;

                for (int i = 0; i < AssetCount; i++)
                {
                    var address = StartAddress + (i * AssetSize);
                    var namePointer = instance.Reader.ReadInt64(address);

                    if (IsNullAsset(namePointer))
                        continue;

                    var buffer = instance.Reader.ReadBytes(address, AssetSize);
                    int segments = BitConverter.ToInt32(buffer, 0x18);
                    float maxLength = BitConverter.ToSingle(buffer, 0x24);

                    results.Add(new Asset()
                    {
                        Name        = instance.Reader.ReadNullTerminatedString(namePointer),
                        Type        = Name,
                        Zone        = ((BlackOps3)instance.Game).ZoneNames.TryGetValue(address, out var zone) ? zone : "unknown",
                        Information = string.Format("{0} Seg, Len {1:0}", segments, maxLength),
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                    });
                }

                return results;
            }

            /// <summary>
            /// Exports the given beam def as a shipping-format beam.gdf GDT
            /// </summary>
            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var buffer = instance.Reader.ReadBytes((long)asset.Data, AssetSize);

                if (asset.Name != instance.Reader.ReadNullTerminatedString(BitConverter.ToInt64(buffer, 0)))
                    throw new Exception("The asset at the expected address has changed");

                var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var assets = new SortedSet<string>(StringComparer.Ordinal);

                foreach (var property in BeamOffsets)
                {
                    var value = DecodeValue(buffer, property.Item2, property.Item3, instance, assets);
                    if (value != null)
                        values[property.Item1] = value;
                }

                var sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("\t\"").Append(asset.Name).Append("\" ( \"beam.gdf\" )\r\n");
                sb.Append("\t{\r\n");
                foreach (var pair in values)
                    sb.Append("\t\t\"").Append(pair.Key).Append("\" \"")
                      .Append(pair.Value.Replace("\\", "\\\\")).Append("\"\r\n");
                sb.Append("\t}\r\n");
                sb.Append("}\r\n");

                var dir = Path.Combine("exported_files", instance.Game.Name, "beams");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, asset.Name + ".gdt"), sb.ToString());

                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                    File.WriteAllText(Path.Combine(dir, asset.Name + "_assets.txt"),
                                      FXEffect.AssetList(asset.Name, assets));
            }

            /// <summary>
            /// Decodes one BeamOffsets entry to its GDT string (the subset of
            /// the stock ConvertAssetBufferToGDTAsset the beam table uses;
            /// asset names resolve via the name pointer at the target's +0)
            /// </summary>
            private string DecodeValue(byte[] buffer, int offset, int type, HydraInstance instance, SortedSet<string> assets)
            {
                string AssetName(long pointer)
                {
                    if (pointer == 0)
                        return "";
                    var namePointer = instance.Reader.ReadInt64(pointer);
                    return namePointer == 0 ? "" : instance.Reader.ReadNullTerminatedString(namePointer);
                }
                string N(float v)
                {
                    if (Math.Abs(v - Math.Round(v)) < 1e-6 && Math.Abs(v) < 1e9)
                        return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
                    return ((double)v).ToString("0.######", CultureInfo.InvariantCulture);
                }

                switch (type)
                {
                    case 0x0:   // string pointer (sound alias name)
                        var strPointer = BitConverter.ToInt64(buffer, offset);
                        return strPointer == 0 ? "" : instance.Reader.ReadNullTerminatedString(strPointer);
                    case 0x4:   // int32
                        return BitConverter.ToInt32(buffer, offset).ToString();
                    case 0x6:   // bool byte
                        return buffer[offset].ToString();
                    case 0x8:   // float
                        return N(BitConverter.ToSingle(buffer, offset));
                    case 0xA:   // fx reference (share/raw path, backslashes doubled at write)
                        var fx = AssetName(BitConverter.ToInt64(buffer, offset));
                        if (fx == "")
                            return "";
                        assets.Add("fx " + fx);
                        return @"fx\" + fx.Replace("/", "\\") + ".efx";
                    case 0x33:  // material (compile-time "el/" category prefix stripped —
                                // source GDTs never carry it; validated 159/159 vs
                                // zod_beast_grapple_beam's shipping GDT)
                        var material = AssetName(BitConverter.ToInt64(buffer, offset));
                        var slash = material.IndexOf('/');
                        if (slash >= 0)
                            material = material.Substring(slash + 1);
                        if (material != "")
                            assets.Add("material " + material);
                        return material;
                    case 0x34:
                        return BeamUVModes[BitConverter.ToInt32(buffer, offset)];
                    case 0x35:
                        return BeamCurveTypes[BitConverter.ToInt32(buffer, offset)];
                    case 0x36:  // RGBA color
                        return string.Format("{0} {1} {2} {3}",
                            N(BitConverter.ToSingle(buffer, offset)),
                            N(BitConverter.ToSingle(buffer, offset + 4)),
                            N(BitConverter.ToSingle(buffer, offset + 8)),
                            N(BitConverter.ToSingle(buffer, offset + 12)));
                    case 0x37:
                        return BeamSlackLoopModes[BitConverter.ToInt32(buffer, offset)];
                    case 0x38:
                        return BeamShapes[BitConverter.ToInt32(buffer, offset)];
                    case 0x39:
                        return BeamCollisionModes[BitConverter.ToInt32(buffer, offset)];
                    case 0x3A:
                        return BeamCollisionMasks.TryGetValue(BitConverter.ToUInt32(buffer, offset), out var mask) ? mask : "";
                    default:
                        return null;
                }
            }

            /// <summary>
            /// Checks if the given asset is a null slot
            /// </summary>
            public bool IsNullAsset(Asset asset)
            {
                return IsNullAsset((long)asset.Data);
            }

            /// <summary>
            /// Checks if the given asset is a null slot
            /// </summary>
            public bool IsNullAsset(long nameAddress)
            {
                return nameAddress >= StartAddress && nameAddress <= EndAddress || nameAddress == 0;
            }
        }
    }
}
