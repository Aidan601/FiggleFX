using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HydraX.Library
{
    partial class BlackOps4
    {
        /// <summary>
        /// Black Ops 4 beam definitions (pool "beam", index 99) — the assets
        /// beamSource/beamTarget fx emitters reference by name, exported as
        /// BO3 beam.gdf GDTs (the C# port of tools/t8_beam_rip.py).
        ///
        /// Struct 0x4D8, mapped 2026-08-07 against the `default_beam` name
        /// anchor (the one def in BOTH the T8 pool and BO3's gdt.db) plus
        /// value-distribution matching vs the 101 BO3 beam GDTs — see repo
        /// CLAUDE.md "T8 beam DEF ripper". Confidence tiers noted inline;
        /// unmapped keys fall to GDF defaults. T8-new width/color curve
        /// arrays and per-wave-family colors have no BO3 key and are not
        /// ported.
        /// </summary>
        private class Beam : IAssetPool
        {
            public int AssetSize { get; set; }
            public int AssetCount { get; set; }
            public long StartAddress { get; set; }
            public long EndAddress { get { return StartAddress + (AssetCount * AssetSize); } set => throw new NotImplementedException(); }
            public string Name => "beam";
            public int Index => 99;

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

                if (!IsCanonicalPointer(StartAddress) || AssetSize <= 0 || AssetSize > 0x10000 || AssetCount <= 0 || AssetCount > 0x100000)
                    return results;

                for (int i = 0; i < AssetCount; i++)
                {
                    var address = StartAddress + (i * AssetSize);
                    var rawHash = (ulong)instance.Reader.ReadInt64(address);

                    // live slots hold an unmasked 64-bit name hash (top nibble
                    // set); free slots are 0 or a freelist pointer into the pool
                    if (rawHash == 0 || (rawHash >> 60) == 0 || IsNullAsset((long)rawHash))
                        continue;

                    var name = GetHashName(rawHash & HashMask, "beam");

                    int segments = instance.Reader.ReadInt32(address + 0x20);
                    float maxLength = BitConverter.ToSingle(instance.Reader.ReadBytes(address + 0x2C, 4), 0);
                    var info = string.Format("{0} Seg, Len {1:0}", segments, maxLength);

                    results.Add(new Asset()
                    {
                        Name        = name,
                        Type        = Name,
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                        Zone        = ZoneOf(instance, address),
                        Information = info
                    });
                }

                return results;
            }

            /// <summary>
            /// Zone attribution. Pool slots sit outside every zone range, so
            /// the def's own streamed curve arrays are tried first — but 18 of
            /// the 38 zm_zodt8 defs have none, and their material ref is a
            /// pool slot too. For those, chase one level into the material
            /// header (stride 0x138) and attribute by ITS streamed data (a
            /// beam's material rides in the same fastfile in practice).
            /// </summary>
            private string ZoneOf(HydraInstance instance, long address)
            {
                var game = (BlackOps4)instance.Game;
                foreach (var off in new[] { 0x68, 0x80, 0x120, 0x168, 0x1F8, 0x200, 0x210, 0x460, 0x470, 0x488, 0x4A0 })
                {
                    var ptr = instance.Reader.ReadInt64(address + off);
                    if (!IsCanonicalPointer(ptr))
                        continue;
                    var zone = game.GetZoneName(ptr);
                    if (zone != "")
                        return zone;
                }

                var material = instance.Reader.ReadInt64(address + 0x10);
                if (IsCanonicalPointer(material))
                {
                    var header = instance.Reader.ReadBytes(material, 0x138);
                    if (header != null)
                    {
                        for (int o = 0; o + 8 <= header.Length; o += 8)
                        {
                            var ptr = BitConverter.ToInt64(header, o);
                            if (!IsCanonicalPointer(ptr))
                                continue;
                            // other pool slots (images, techsets) return "" and
                            // are skipped; the first real heap block names the zone
                            var zone = game.GetZoneName(ptr);
                            if (zone != "")
                                return zone;
                        }
                    }
                }
                return "";
            }

            /// <summary>
            /// Exports the given beam def as a BO3 beam.gdf GDT
            /// </summary>
            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var d = instance.Reader.ReadBytes((long)asset.Data, AssetSize);
                if (d == null || d.Length < AssetSize)
                    throw new Exception("Failed to read beam def");

                float F(int off) => BitConverter.ToSingle(d, off);
                int I(int off) => BitConverter.ToInt32(d, off);
                string N(float v)
                {
                    if (Math.Abs(v - Math.Round(v)) < 1e-6 && Math.Abs(v) < 1e9)
                        return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
                    return ((double)v).ToString("0.######", CultureInfo.InvariantCulture);
                }

                var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var assets = new SortedSet<string>(StringComparer.Ordinal);

                // ---- refs (chase-verified: material 38/38, perp 9/38) ----
                var material = MaterialName(instance, BitConverter.ToInt64(d, 0x10));
                if (material != null)
                {
                    values["material"] = material;
                    assets.Add("material " + material);
                }
                var perp = MaterialName(instance, BitConverter.ToInt64(d, 0x18));
                if (perp != null)
                {
                    values["perpendicularMaterial"] = perp;
                    values["drawPerpendicularCard"] = "1";
                    assets.Add("material " + perp);
                }
                // fx refs in BO3 struct order; +0x410 is the dead slot (BO3
                // deathEffect 0/101 used and T8 +0x410 0/38 pins the order;
                // origin-vs-target orientation is MEDIUM confidence)
                foreach (var slot in new[] { (0x3F8, "originEffect"), (0x400, "targetEffect"),
                                             (0x408, "deathEffect"), (0x418, "beamEffect"),
                                             (0x420, "blockedEffect") })
                {
                    var fx = FxName(instance, BitConverter.ToInt64(d, slot.Item1));
                    if (fx != null)
                    {
                        values[slot.Item2] = FxPath(fx);
                        assets.Add("fx " + fx);
                    }
                }

                // ---- core scalars (HIGH: anchor + distribution match) ----
                values["numSegments"] = I(0x20).ToString();
                values["useVirtualTarget"] = (I(0x24) & 1).ToString();
                values["msecAnimLoopTime"] = I(0x28).ToString();
                values["maxLength"] = N(F(0x2C));
                values["virtualTargetDistance"] = N(F(0x30));
                values["virtualTargetMass"] = N(F(0x34));
                values["beamInitialSpeed"] = N(F(0x3C));
                values["textureRepeatLength"] = N(F(0x90));
                values["beamEffectDistance"] = N(F(0xAC));
                values["beamEffectSpeed"] = N(F(0xB0));
                values["retractSpeed"] = N(F(0xBC));
                values["slackStartTimeMsec"] = I(0x3DC).ToString();
                values["slackEaseInTimeMsec"] = I(0x3E0).ToString();
                values["slackDurationMsec"] = I(0x3E4).ToString();
                values["slackEaseOutTimeMsec"] = I(0x3E8).ToString();
                values["slackMinSlack"] = N(F(0x3F0));

                // widths + leadout (MEDIUM: distribution top-hit; T8 also has
                // width curves in heap arrays — these scalars are the base look)
                values["startWidth"] = N(F(0x54));
                values["endWidth"] = N(F(0x58));
                values["leadoutDistance"] = N(F(0x94));

                // base color RGBA at +0x1F0 (T8 animates color via curves we
                // don't port — emit the base for both ends)
                var col = string.Format("{0} {1} {2} {3}", N(F(0x1F0)), N(F(0x1F4)), N(F(0x1F8)), N(F(0x1FC)));
                values["startColor"] = col;
                values["endColor"] = col;

                // ---- wave families {ampX/Y, freqX/Y, speedX/Y} ----
                foreach (var fam in new[] { ("sine", 0x220, 0x24C, 0x278), ("sawtooth", 0x280, 0x2AC, 0x2DC),
                                            ("square", 0x2E4, 0x310, 0x340), ("triangle", 0x348, 0x374, 0x3A4) })
                {
                    values[fam.Item1 + "AmplitudeX"] = N(F(fam.Item2));
                    values[fam.Item1 + "AmplitudeY"] = N(F(fam.Item2 + 4));
                    values[fam.Item1 + "FrequencyX"] = N(F(fam.Item3));
                    values[fam.Item1 + "FrequencyY"] = N(F(fam.Item3 + 4));
                    values[fam.Item1 + "SpeedX"] = N(F(fam.Item4));
                    values[fam.Item1 + "SpeedY"] = N(F(fam.Item4 + 4));
                }

                // ---- emit (shipping GDT format, CRLF, backslashes doubled) ----
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

            private static string Bo3FxRoot;

            /// <summary>
            /// BO4 fx name -> the share/raw path BO3 GDTs use. Effects BO3
            /// already ships keep their path; everything else is addressed
            /// under share/raw/fx/t8/ (where ported .efx exports go). The BO3
            /// install is found via TA_TOOLS_PATH (set by the mod tools);
            /// without it every effect gets the t8/ prefix.
            /// </summary>
            private static string FxPath(string name)
            {
                if (Bo3FxRoot == null)
                {
                    var root = Environment.GetEnvironmentVariable("TA_TOOLS_PATH") ?? "";
                    Bo3FxRoot = root.Length > 0 ? Path.Combine(root, "share", "raw", "fx") : "";
                }

                var relative = name.Replace('/', '\\');
                bool owned = false;
                try
                {
                    owned = Bo3FxRoot.Length > 0 && File.Exists(Path.Combine(Bo3FxRoot, relative + ".efx"));
                }
                catch { }

                return "fx\\" + (owned ? relative : "t8\\" + relative) + ".efx";
            }

            /// <summary>
            /// Resolves a material asset pointer to its source name (hash at
            /// +0; compile-time "el/" category prefix stripped)
            /// </summary>
            private static string MaterialName(HydraInstance instance, long address)
            {
                if (!IsCanonicalPointer(address))
                    return null;
                var hash = (ulong)instance.Reader.ReadInt64(address) & HashMask;
                if (hash == 0)
                    return null;
                var name = GetHashName(hash, "material");
                var slash = name.IndexOf('/');
                return slash >= 0 ? name.Substring(slash + 1) : name;
            }

            /// <summary>
            /// Resolves an fx asset pointer to its name (hash at +0)
            /// </summary>
            private static string FxName(HydraInstance instance, long address)
            {
                if (!IsCanonicalPointer(address))
                    return null;
                var hash = (ulong)instance.Reader.ReadInt64(address) & HashMask;
                return hash == 0 ? null : GetHashName(hash, "fx");
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
