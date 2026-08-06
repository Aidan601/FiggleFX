using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HydraX.Library
{
    partial class BlackOps3
    {
        /// <summary>
        /// Black Ops 3 lens flare definitions (pool "klf", index 40).
        ///
        /// Source form is share/raw/lensflares/*.klf: a fixed-schema text file
        /// (gradientDefVersion 1 / elemDefVersion 11 / defVersion 6) with a
        /// name, a uuid, a flare type, a grime texture and an elems[] array of
        /// 158-key flare sprites. Effects reference a flare by its UUID — the
        /// 16-byte GUID sits inline in the FxElemDef (T7 +0xF0, T8 +0x00).
        ///
        /// Compiled layout (mapped 2026-08-05 against the 5 shipping-source
        /// anchor flares loaded in the live session; see tools/map_klf.py):
        ///   header 0xA8: +0x00 uuid string ptr, +0x08 elem array ptr,
        ///   +0x2C u32 elem count (dontExport-style: exportable-0 elems are
        ///   stripped at compile), +0x98 grime image asset ptr.
        ///   elem stride 0x1F0: +0x08 count, offset-gradient pair +0x44..0x68
        ///   (interleaved g1/g2 SoA: centerPos, edgePos, centerValue,
        ///   edgeValue, power), scale-gradient pair +0xB8..0xDC (same shape),
        ///   +0xE0/+0xF0 X/Y scale x50, color-gradient pair +0x10C..0x130,
        ///   +0x134 colorStrength, +0x144 color RGB (f32 x255),
        ///   +0x1E0 image asset ptr (name at image+0xF8),
        ///   +0x1E8 colorIntensity, +0x1EC grimeIntensity.
        /// Unmapped keys are emitted with the corpus-modal defaults of the 24
        /// shipping .klf files — see the LIMITATIONS note in ExportAsset.
        /// </summary>
        private class KLFDef : IAssetPool
        {
            /// <summary>
            /// When set, ExportAsset also writes the raw research dumps
            /// (.header.bin/.ptrs.*) used for offline field mapping
            /// (KlfDump.exe --raw)
            /// </summary>
            public static bool RawDumps = false;

            /// <summary>
            /// Bytes read from each pointer target that is not a string
            /// </summary>
            public static int PointerChaseSize = 0x4000;

            /// <summary>
            /// How many pointer levels to follow out of the asset header
            /// </summary>
            public static int ChaseDepth = 2;

            /// <summary>
            /// Safety cap on chased blocks per asset
            /// </summary>
            public static int MaxChases = 512;

            public int AssetSize { get; set; }
            public int AssetCount { get; set; }
            public long StartAddress { get; set; }
            public long EndAddress { get { return StartAddress + (AssetCount * AssetSize); } set => throw new NotImplementedException(); }
            public string Name => "lensflare";
            public int Index => (int)AssetPool.klf;

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

                    // the name field is assumed to be at +0 like every other BO3
                    // pool; if that turns out to be wrong the asset is still
                    // listed (and dumpable) under a slot-index placeholder
                    var name = ReadName(instance, namePointer) ?? ("klf_" + i);

                    // +0x64 elem records, +0x2C total sprites (an elem can draw
                    // several), +0xA0 flare type — see the layout notes above
                    var header = instance.Reader.ReadBytes(address, AssetSize);
                    string info;
                    if (header == null || header.Length < AssetSize)
                    {
                        info = "Unreadable";
                    }
                    else
                    {
                        int elems = BitConverter.ToInt32(header, 0x64);
                        int sprites = BitConverter.ToInt32(header, 0x2C);
                        info = elems + (elems == 1 ? " Element" : " Elements");
                        if (sprites != elems)
                            info += ", " + sprites + (sprites == 1 ? " Sprite" : " Sprites");
                        info += BitConverter.ToInt32(header, 0xA0) == 1 ? ", Directional" : ", Radial";
                    }

                    results.Add(new Asset()
                    {
                        Name        = name,
                        Type        = Name,
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                        Zone        = ((BlackOps3)instance.Game).ZoneNames.TryGetValue(address, out var zone) ? zone : "unknown",
                        Information = info
                    });
                }

                return results;
            }

            /// <summary>
            /// Exports the given asset as a decompiled .klf source file.
            ///
            /// LIMITATIONS (v1): the flare name is not stored in memory (only
            /// the uuid), so name==uuid; elem names become elem0..N; type is
            /// always RADIAL (the DIRECTIONAL discriminator is unmapped);
            /// seeds, colorDepth*, taper*, position/offset min-max, rotation
            /// and the mode/order enums are emitted as the corpus-modal
            /// defaults of the shipping .klf set; checksum is written as 0.
            /// </summary>
            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var address = (long)asset.Data;
                var header = ReadChunked(instance, address, AssetSize);

                var dir = Path.Combine("exported_files", instance.Game.Name, "lensflares");
                Directory.CreateDirectory(dir);

                var assets = new SortedSet<string>(StringComparer.Ordinal);
                var text = Decompile(instance, asset.Name, header, assets);
                // shipping .klf files are CRLF, and Radiant's parsers reject
                // LF-only files (same lesson as .efx)
                File.WriteAllText(Path.Combine(dir, Sanitize(asset.Name) + ".klf"), text.Replace("\n", "\r\n"));

                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                    File.WriteAllText(Path.Combine(dir, Sanitize(asset.Name) + "_assets.txt"),
                                      FXEffect.AssetList(asset.Name, assets));

                if (RawDumps)
                {
                    var rawDir = Path.Combine("exported_files", instance.Game.Name, "lensflares_raw");
                    Directory.CreateDirectory(rawDir);
                    var baseName = Path.Combine(rawDir, Sanitize(asset.Name));
                    File.WriteAllBytes(baseName + ".header.bin", header);
                    File.WriteAllText(baseName + ".info.txt", Annotate(instance, asset, address, header));
                    DumpPointerTargets(instance, baseName, header);
                }
            }

            /// <summary>
            /// Elem record stride in the compiled flare
            /// </summary>
            private const int ElemSize = 0x1F0;

            private string Decompile(HydraInstance instance, string name, byte[] header, SortedSet<string> assets)
            {
                var sb = new StringBuilder();
                // +0x64 = elem record count; +0x2C is the TOTAL sprite count
                // (sum of the elems' `count` keys — equal only when all are 1)
                int elemCount = BitConverter.ToInt32(header, 0x64);
                long elemsPtr = BitConverter.ToInt64(header, 0x08);
                string grime = ImageName(instance, BitConverter.ToInt64(header, 0x98)) ?? "$white";
                if (grime != "$white")
                    assets.Add("image " + grime);

                // header +0xA0: 2 = RADIAL, 1 = DIRECTIONAL (11/11 anchors)
                string type = BitConverter.ToInt32(header, 0xA0) == 1
                    ? "FX_LENSFLARE_DIRECTIONAL" : "FX_LENSFLARE_RADIAL";

                sb.Append("gradientDefVersion 1\n");
                sb.Append("elemDefVersion 11\n");
                sb.Append("defVersion 6\n");
                sb.Append("{\n");
                sb.Append("    name \"").Append(name).Append("\"\n");
                sb.Append("    uuid \"").Append(name).Append("\"\n");
                sb.Append("    type ").Append(type).Append('\n');
                sb.Append("    grimeName \"").Append(grime).Append("\"\n");
                sb.Append("    elems [\n");

                for (int i = 0; i < elemCount; i++)
                {
                    var e = ReadChunked(instance, elemsPtr + i * ElemSize, ElemSize);
                    WriteElem(sb, instance, e, i, assets);
                }

                sb.Append("    ]\n");
                sb.Append("    offscreenBufferSize 0\n");
                sb.Append("    checksum 0\n");
                sb.Append("    exportable 1\n");
                sb.Append("    invisible 0\n");
                sb.Append("}\n");
                return sb.ToString();
            }

            private void WriteElem(StringBuilder sb, HydraInstance instance, byte[] e, int index, SortedSet<string> assets)
            {
                float F(int off) => BitConverter.ToSingle(e, off);
                int I(int off) => BitConverter.ToInt32(e, off);
                string N(float v)
                {
                    if (Math.Abs(v - Math.Round(v)) < 1e-6 && Math.Abs(v) < 1e9)
                        return ((long)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return ((double)v).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                }

                string texture = ImageName(instance, BitConverter.ToInt64(e, 0x1E0)) ?? "$white";
                if (texture != "$white")
                    assets.Add("image " + texture);

                // gradient pairs are interleaved g1/g2 SoA blocks:
                // centerPos, edgePos, centerValue, edgeValue, power
                void Gradient(string prefix, int baseOff, string mode)
                {
                    for (int g = 0; g < 2; g++)
                    {
                        float centerPos = F(baseOff + 0x00 + g * 4);
                        float edgePos = F(baseOff + 0x08 + g * 4);
                        float centerValue = F(baseOff + 0x10 + g * 4);
                        float edgeValue = F(baseOff + 0x18 + g * 4);
                        float power = F(baseOff + 0x20 + g * 4);
                        // a gradient that stayed at the neutral values compiled
                        // from NO_GRADIENT; anything else needed PROCEDURAL
                        bool active = Math.Abs(centerValue - 1) > 1e-6 || Math.Abs(edgeValue - 1) > 1e-6;
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("Type ")
                          .Append(active ? "FX_LENSFLARE_PROCEDURAL_GRADIENT" : "FX_LENSFLARE_NO_GRADIENT").Append('\n');
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("Mode ").Append(mode).Append('\n');
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("CenterValue ").Append(N(centerValue)).Append('\n');
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("CenterPos ").Append(N(centerPos)).Append('\n');
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("Power ").Append(N(power)).Append('\n');
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("EdgeValue ").Append(N(edgeValue)).Append('\n');
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("EdgePos ").Append(N(edgePos)).Append('\n');
                        sb.Append("            ").Append(prefix).Append(g + 1).Append("TextureName \"$white\"\n");
                    }
                    sb.Append("            ").Append(prefix).Append("MixMode FX_LENSFLARE_GRADIENT_MULTIPLY\n");
                }

                sb.Append("        {\n");
                sb.Append("            name \"elem").Append(index).Append("\"\n");
                sb.Append("            count ").Append(I(0x08)).Append('\n');
                sb.Append("            textureName \"").Append(texture).Append("\"\n");
                sb.Append("            seed [\n                10\n                20\n                30\n                40\n            ]\n");
                sb.Append("            seedLocked [\n                0\n                0\n                0\n                0\n            ]\n");
                sb.Append("            order FX_LENSFLARE_ORDER_SRPO\n");
                sb.Append("            colorIntensity ").Append(N(F(0x1E8))).Append('\n');
                sb.Append("            grimeIntensity ").Append(N(F(0x1EC))).Append('\n');
                sb.Append("            color [\n");
                for (int c = 0; c < 3; c++)
                    sb.Append("                ").Append((int)Math.Round(F(0x144 + c * 4) * 255f)).Append('\n');
                sb.Append("            ]\n");
                sb.Append("            colorMode FX_LENSFLARE_FIXED\n");
                sb.Append("            colorSeedIndex 0\n");
                sb.Append("            colorVariance 0\n");
                sb.Append("            minColorStrength ").Append(N(F(0x134))).Append('\n');
                sb.Append("            maxColorStrength ").Append(N(F(0x134))).Append('\n');
                sb.Append("            previousMaxColorStrength 1\n");
                sb.Append("            colorDepthEnabled 1\n");
                sb.Append("            colorDepthBeginFadeIn 0\n");
                sb.Append("            colorDepthEndFadeIn 0\n");
                sb.Append("            colorDepthBeginFadeOut 0\n");
                sb.Append("            colorDepthEndFadeOut 0\n");
                sb.Append("            colorDepthPower 1\n");
                sb.Append("            colorDepthMin 0\n");
                sb.Append("            colorDepthMax 1\n");
                Gradient("colorGradient", 0x10C, "FX_LENSFLARE_GRADIENT_SOURCE_TO_SCREEN");
                sb.Append("            maxTaper 0\n");
                for (int g = 1; g <= 2; g++)
                {
                    sb.Append("            taperGradient").Append(g).Append("Type FX_LENSFLARE_NO_GRADIENT\n");
                    sb.Append("            taperGradient").Append(g).Append("Mode FX_LENSFLARE_GRADIENT_SPRITE_TO_SCREEN\n");
                    sb.Append("            taperGradient").Append(g).Append("CenterValue ").Append(g == 1 ? 0 : 1).Append('\n');
                    sb.Append("            taperGradient").Append(g).Append("CenterPos 0\n");
                    sb.Append("            taperGradient").Append(g).Append("Power 2\n");
                    sb.Append("            taperGradient").Append(g).Append("EdgeValue ").Append(g == 1 ? 1 : 0).Append('\n');
                    sb.Append("            taperGradient").Append(g).Append("EdgePos 1\n");
                    sb.Append("            taperGradient").Append(g).Append("TextureName \"$white\"\n");
                }
                sb.Append("            taperGradientMixMode FX_LENSFLARE_GRADIENT_MULTIPLY\n");
                sb.Append("            positionType FX_LENSFLARE_POSITION_RAY_RELATIVE\n");
                sb.Append("            xPositionMode FX_LENSFLARE_FIXED\n");
                sb.Append("            yPositionMode FX_LENSFLARE_FIXED\n");
                sb.Append("            xPositionSeedIndex 1\n");
                sb.Append("            yPositionSeedIndex 1\n");
                sb.Append("            positionIsConstrained 1\n");
                sb.Append("            xPositionVariance 0\n");
                sb.Append("            yPositionVariance 0\n");
                sb.Append("            minXPosition 0\n");
                sb.Append("            maxXPosition 10\n");
                sb.Append("            minYPosition 0\n");
                sb.Append("            maxYPosition 10\n");
                sb.Append("            previousMaxXPosition 10\n");
                sb.Append("            previousMinYPosition 0\n");
                sb.Append("            previousMaxYPosition 10\n");
                sb.Append("            previousYPositionMode FX_LENSFLARE_FIXED\n");
                sb.Append("            previousYPositionSeedIndex 1\n");
                sb.Append("            previousYPositionVariance 0\n");
                sb.Append("            offsetType FX_LENSFLARE_SCREEN_OFFSET\n");
                sb.Append("            xOffsetMode FX_LENSFLARE_FIXED\n");
                sb.Append("            yOffsetMode FX_LENSFLARE_FIXED\n");
                sb.Append("            xOffsetSeedIndex 2\n");
                sb.Append("            yOffsetSeedIndex 1\n");
                sb.Append("            offsetIsConstrained 1\n");
                sb.Append("            xOffsetVariance 0\n");
                sb.Append("            yOffsetVariance 0\n");
                sb.Append("            minXOffset 0\n");
                sb.Append("            maxXOffset 10\n");
                sb.Append("            minYOffset ").Append(N(F(0x80) * 50f)).Append('\n');
                sb.Append("            maxYOffset 10\n");
                sb.Append("            previousMaxXOffset 10\n");
                sb.Append("            previousMinYOffset 0\n");
                sb.Append("            previousMaxYOffset 10\n");
                sb.Append("            previousYOffsetMode FX_LENSFLARE_FIXED\n");
                sb.Append("            previousYOffsetSeedIndex 1\n");
                sb.Append("            previousYOffsetVariance 0\n");
                Gradient("offsetGradient", 0x44, "FX_LENSFLARE_GRADIENT_SOURCE_TO_SCREEN");
                sb.Append("            scaleType FX_LENSFLARE_SCREEN_SCALE\n");
                sb.Append("            xScaleMode FX_LENSFLARE_FIXED\n");
                sb.Append("            yScaleMode FX_LENSFLARE_FIXED\n");
                sb.Append("            xScaleSeedIndex 3\n");
                sb.Append("            yScaleSeedIndex 3\n");
                sb.Append("            xScaleVariance 0\n");
                sb.Append("            yScaleVariance 0\n");
                sb.Append("            scaleIsMirrored 0\n");
                sb.Append("            scaleRatio FX_LENSFLARE_RATIO_OFF\n");
                sb.Append("            previousScaleRatio FX_LENSFLARE_RATIO_OFF\n");
                // scales are stored /50; +0xE8 = X delta (max-min)
                float maxXScale = (F(0xE0) + F(0xE8)) * 50f;
                sb.Append("            minXScale ").Append(N(F(0xE0) * 50f)).Append('\n');
                sb.Append("            maxXScale ").Append(N(maxXScale)).Append('\n');
                sb.Append("            minYScale ").Append(N(F(0xF0) * 50f)).Append('\n');
                sb.Append("            maxYScale ").Append(N(F(0xF0) * 50f)).Append('\n');
                sb.Append("            previousMaxXScale ").Append(N(maxXScale)).Append('\n');
                sb.Append("            previousMinYScale ").Append(N(F(0xF0) * 50f)).Append('\n');
                sb.Append("            previousMaxYScale ").Append(N(F(0xF0) * 50f)).Append('\n');
                sb.Append("            previousYScaleMode FX_LENSFLARE_FIXED\n");
                sb.Append("            previousYScaleSeedIndex 1\n");
                sb.Append("            previousYScaleVariance 0\n");
                Gradient("scaleGradient", 0xB8, "FX_LENSFLARE_GRADIENT_SOURCE_TO_SCREEN");
                // +0x94 = minAngle in radians (39/39). maxAngle is NOT stored
                // as a scalar (baked into the runtime data): a nonzero min is
                // a fixed angle (min==max in every such source), a zero min is
                // almost always the free 0-360 spin — emit that convention.
                float minAngle = (float)Math.Round(F(0x94) * 180.0 / Math.PI, 3);
                float maxAngle = minAngle != 0 ? minAngle : 360;
                sb.Append("            rotationFacing FX_LENSFLARE_FACING_NONE\n");
                sb.Append("            rotationMode FX_LENSFLARE_FIXED\n");
                sb.Append("            rotationSeedIndex 0\n");
                sb.Append("            rotationVariance 0\n");
                sb.Append("            minAngle ").Append(N(minAngle)).Append('\n');
                sb.Append("            maxAngle ").Append(N(maxAngle)).Append('\n');
                sb.Append("            FOVMultiplier 1\n");
                sb.Append("            previousMaxAngle 360\n");
                sb.Append("            invisible 0\n");
                sb.Append("            exportable 1\n");
                sb.Append("        }\n");
            }

            /// <summary>
            /// Resolves a GfxImage asset pointer to its name (name ptr at
            /// image+0xF8, established against the flare grime textures)
            /// </summary>
            private static string ImageName(HydraInstance instance, long imageAddress)
            {
                if (!IsCanonicalPointer(imageAddress) && !(imageAddress > 0x7FF000000000))
                    return null;
                var namePtr = instance.Reader.ReadInt64(imageAddress + 0xF8);
                return ReadName(instance, namePtr);
            }

            /// <summary>
            /// Multi-representation view of the asset header
            /// </summary>
            private string Annotate(HydraInstance instance, Asset asset, long address, byte[] header)
            {
                var sb = new StringBuilder();
                sb.AppendFormat("name {0}\naddress 0x{1:X}\nsize 0x{2:X}\nzone {3}\n\n",
                    asset.Name, address, header.Length, asset.Zone);

                for (int off = 0; off + 8 <= header.Length; off += 8)
                {
                    var u64 = BitConverter.ToInt64(header, off);
                    sb.AppendFormat("+0x{0:X3}  {1:X16}  i32: {2,11} {3,11}  f32: {4,14:G6} {5,14:G6}",
                        off, u64,
                        BitConverter.ToInt32(header, off), BitConverter.ToInt32(header, off + 4),
                        BitConverter.ToSingle(header, off), BitConverter.ToSingle(header, off + 4));

                    if (IsCanonicalPointer(u64))
                    {
                        sb.Append("  PTR");
                        var text = ReadName(instance, u64);
                        if (text != null)
                            sb.AppendFormat(" \"{0}\"", text);
                    }

                    sb.Append('\n');
                }

                return sb.ToString();
            }

            /// <summary>
            /// Follows every pointer out of the header (and, one level deeper,
            /// out of each chased block). String targets are logged inline;
            /// everything else is written to the .ptrs.bin blob.
            /// </summary>
            private void DumpPointerTargets(HydraInstance instance, string baseName, byte[] header)
            {
                var seen = new HashSet<long>();
                var index = new StringBuilder();
                var strings = new StringBuilder();
                int chases = 0;

                using (var stream = File.Create(baseName + ".ptrs.bin"))
                {
                    // (label, buffer) pairs still to be walked
                    var queue = new Queue<Tuple<string, byte[], int>>();
                    queue.Enqueue(Tuple.Create("hdr", header, 0));

                    while (queue.Count > 0)
                    {
                        var item = queue.Dequeue();
                        var label = item.Item1;
                        var buffer = item.Item2;
                        var depth = item.Item3;

                        for (int off = 0; off + 8 <= buffer.Length; off += 8)
                        {
                            var target = BitConverter.ToInt64(buffer, off);

                            if (!IsCanonicalPointer(target) || !seen.Add(target))
                                continue;

                            var text = ReadName(instance, target);
                            if (text != null)
                            {
                                strings.AppendFormat("{0}+0x{1:X4} -> 0x{2:X}  \"{3}\"\n", label, off, target, text);
                                continue;
                            }

                            if (chases >= MaxChases)
                                continue;
                            chases++;

                            var data = ReadChunked(instance, target, PointerChaseSize);
                            index.AppendFormat("{0}+0x{1:X4} -> 0x{2:X} @ file 0x{3:X} len 0x{4:X}\n",
                                label, off, target, stream.Position, data.Length);
                            stream.Write(data, 0, data.Length);

                            if (depth + 1 < ChaseDepth)
                                queue.Enqueue(Tuple.Create(string.Format("{0}+0x{1:X4}", label, off), data, depth + 1));
                        }
                    }
                }

                File.WriteAllText(baseName + ".ptrs.txt", index.ToString());
                File.WriteAllText(baseName + ".strings.txt", strings.ToString());
            }

            /// <summary>
            /// Reads a printable null-terminated string, or null if the target
            /// isn't one (used both for names and to classify chase targets)
            /// </summary>
            private static string ReadName(HydraInstance instance, long address)
            {
                if (!IsCanonicalPointer(address))
                    return null;

                var data = instance.Reader.ReadBytes(address, 128);
                if (data == null || data.Length == 0)
                    return null;

                int length = 0;
                while (length < data.Length && data[length] != 0)
                {
                    if (data[length] < 0x20 || data[length] > 0x7E)
                        return null;
                    length++;
                }

                // a struct starting with a small int reads as a 1-2 char
                // "string"; requiring a few characters keeps those chaseable
                if (length < 3 || length >= data.Length)
                    return null;

                return Encoding.ASCII.GetString(data, 0, length);
            }

            private static bool IsCanonicalPointer(long value)
            {
                return value > 0x10000000000 && value < 0x7FFFFFFFFFFF;
            }

            private static string Sanitize(string name)
            {
                var sb = new StringBuilder(name.Length);
                foreach (var ch in name)
                    sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
                return sb.ToString();
            }

            /// <summary>
            /// Page-chunked read: ReadProcessMemory zero-fills a whole failed
            /// range, so a single unmapped page must not lose the rest
            /// </summary>
            private static byte[] ReadChunked(HydraInstance instance, long address, int size)
            {
                var result = new byte[size];
                for (int done = 0; done < size;)
                {
                    int chunk = Math.Min(0x1000 - (int)((address + done) & 0xFFF), size - done);
                    var data = instance.Reader.ReadBytes(address + done, chunk);
                    if (data != null)
                        Buffer.BlockCopy(data, 0, result, done, Math.Min(chunk, data.Length));
                    done += chunk;
                }
                return result;
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
