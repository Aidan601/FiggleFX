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
        /// Black Ops 4 lens flare definitions (pool "klf", index 35) —
        /// decompiled to BO3-format .klf source files (usable directly in the
        /// BO3 mod tools; effects bind to flares by the uuid key).
        ///
        /// Compiled layout (2026-08-05, validated against the 6 flares whose
        /// uuid matches a shipped BO3 .klf — 88.7% key agreement, residuals
        /// are genuine BO4 retunes; see repo CLAUDE.md "T8 (BO4) klf"):
        ///   header 0xD8: +0x00 uuid string ptr, +0x08 fnv1a60(uuid)
        ///   (= pool asset name), +0x18 fnv1a60(real lf_* flare name),
        ///   +0x28 elem array ptr, +0xC0 u16 elem count, +0xC8 grime image
        ///   asset ptr, +0xD0 type (2=RADIAL, 1=DIRECTIONAL).
        ///   elem 0x2D8: +0x08 count, offset/scale gradients split into X/Y
        ///   pairs (+0xAC/+0xE0, +0x164/+0x198), +0x130 minAngle rad,
        ///   +0x1C0/+0x1D0 X/Y scale /50 (+0x1C8/+0x1D8 deltas), color
        ///   gradient @+0x1EC, +0x214 colorStrength, +0x224 color RGB f32,
        ///   +0x2C8 texture image asset ptr, +0x2D0/+0x2D4 intensities.
        /// Unmapped keys (colorDepth fades, position/offset ranges, enums)
        /// are emitted as corpus-modal defaults, like the BO3 exporter.
        /// </summary>
        private class KLFDef : IAssetPool
        {
            /// <summary>
            /// Elem record stride in the compiled flare
            /// </summary>
            private const int ElemSize = 0x2D8;

            public int AssetSize { get; set; }
            public int AssetCount { get; set; }
            public long StartAddress { get; set; }
            public long EndAddress { get { return StartAddress + (AssetCount * AssetSize); } set => throw new NotImplementedException(); }
            public string Name => "lensflare";
            public int Index => 35;

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
                    var uuidPointer = instance.Reader.ReadInt64(address);

                    if (IsNullAsset(uuidPointer))
                        continue;

                    var uuid = instance.Reader.ReadNullTerminatedString(uuidPointer);
                    if (string.IsNullOrEmpty(uuid) || uuid == "missing")
                        continue;

                    // +0x18 hashes the flare's real lf_* name (BO3 lost these
                    // at compile; T8 kept the hash) — display it when the
                    // dictionary resolves it, else fall back to the uuid
                    var nameHash = (ulong)instance.Reader.ReadInt64(address + 0x18) & HashMask;
                    var name = GetHashName(nameHash, "klf");
                    if (name.StartsWith("klf_"))
                        name = uuid;

                    // +0xC0 elem records, +0xD0 flare type (2 radial, 1 directional)
                    int elems = instance.Reader.ReadUInt16(address + 0xC0);
                    var info = elems + (elems == 1 ? " Element" : " Elements")
                             + (instance.Reader.ReadInt32(address + 0xD0) == 1 ? ", Directional" : ", Radial");

                    results.Add(new Asset()
                    {
                        Name        = name,
                        Type        = Name,
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                        // Zone comes from where the elem array was streamed to,
                        // not the header (pool slots sit outside every zone)
                        Zone        = ((BlackOps4)instance.Game).GetZoneName(instance.Reader.ReadInt64(address + 0x28)),
                        Information = info
                    });
                }

                return results;
            }

            /// <summary>
            /// Exports the given asset as a decompiled BO3-format .klf file
            /// </summary>
            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var address = (long)asset.Data;
                var header = instance.Reader.ReadBytes(address, AssetSize);

                var dir = Path.Combine("exported_files", instance.Game.Name, "lensflares");
                Directory.CreateDirectory(dir);

                var assets = new SortedSet<string>(StringComparer.Ordinal);
                var text = Decompile(instance, asset.Name, header, assets);
                // Radiant's parsers require CRLF (validated live 2026-08-05)
                File.WriteAllText(Path.Combine(dir, Sanitize(asset.Name) + ".klf"), text.Replace("\n", "\r\n"));

                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                    File.WriteAllText(Path.Combine(dir, Sanitize(asset.Name) + "_assets.txt"),
                                      FXEffect.AssetList(asset.Name, assets));
            }

            private string Decompile(HydraInstance instance, string name, byte[] header, SortedSet<string> assets)
            {
                var sb = new StringBuilder();

                string uuid = instance.Reader.ReadNullTerminatedString(BitConverter.ToInt64(header, 0x00));
                int elemCount = BitConverter.ToUInt16(header, 0xC0);
                long elemsPtr = BitConverter.ToInt64(header, 0x28);
                string grime = ImageName(instance, BitConverter.ToInt64(header, 0xC8)) ?? "$white";
                if (grime != "$white")
                    assets.Add("image " + grime);
                string type = BitConverter.ToInt32(header, 0xD0) == 1
                    ? "FX_LENSFLARE_DIRECTIONAL" : "FX_LENSFLARE_RADIAL";

                sb.Append("gradientDefVersion 1\n");
                sb.Append("elemDefVersion 11\n");
                sb.Append("defVersion 6\n");
                sb.Append("{\n");
                sb.Append("    name \"").Append(name).Append("\"\n");
                sb.Append("    uuid \"").Append(uuid).Append("\"\n");
                sb.Append("    type ").Append(type).Append('\n');
                sb.Append("    grimeName \"").Append(grime).Append("\"\n");
                sb.Append("    elems [\n");

                for (int i = 0; i < elemCount; i++)
                {
                    var e = instance.Reader.ReadBytes(elemsPtr + i * ElemSize, ElemSize);
                    if (e == null || e.Length < ElemSize)
                        break;
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
                        return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
                    return ((double)v).ToString("0.######", CultureInfo.InvariantCulture);
                }

                string texture = ImageName(instance, BitConverter.ToInt64(e, 0x2C8)) ?? "$white";
                if (texture != "$white")
                    assets.Add("image " + texture);

                // gradient pairs are interleaved g1/g2 SoA blocks (same shape
                // as BO3): centerPos x2, edgePos x2, centerValue x2,
                // edgeValue x2, power x2. T8 splits offset/scale gradients
                // into X and Y sets 0x34 apart; the source format has one
                // set, so the X gradient is emitted.
                void Gradient(string prefix, int baseOff, string mode)
                {
                    for (int g = 0; g < 2; g++)
                    {
                        float centerPos = F(baseOff + 0x00 + g * 4);
                        float edgePos = F(baseOff + 0x08 + g * 4);
                        float centerValue = F(baseOff + 0x10 + g * 4);
                        float edgeValue = F(baseOff + 0x18 + g * 4);
                        float power = F(baseOff + 0x20 + g * 4);
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

                float minAngle = (float)Math.Round(F(0x130) * 180.0 / Math.PI, 3);
                float maxAngle = minAngle != 0 ? minAngle : 360;
                float minXScale = F(0x1C0) * 50f, maxXScale = (F(0x1C0) + F(0x1C8)) * 50f;
                float minYScale = F(0x1D0) * 50f, maxYScale = (F(0x1D0) + F(0x1D8)) * 50f;
                float strength = F(0x214);

                sb.Append("        {\n");
                sb.Append("            name \"elem").Append(index).Append("\"\n");
                sb.Append("            count ").Append(I(0x08)).Append('\n');
                sb.Append("            textureName \"").Append(texture).Append("\"\n");
                sb.Append("            seed [\n                10\n                20\n                30\n                40\n            ]\n");
                sb.Append("            seedLocked [\n                0\n                0\n                0\n                0\n            ]\n");
                sb.Append("            order FX_LENSFLARE_ORDER_SRPO\n");
                sb.Append("            colorIntensity ").Append(N(F(0x2D0))).Append('\n');
                sb.Append("            grimeIntensity ").Append(N(F(0x2D4))).Append('\n');
                sb.Append("            color [\n");
                for (int c = 0; c < 3; c++)
                    sb.Append("                ").Append((int)Math.Round(F(0x224 + c * 4) * 255f)).Append('\n');
                sb.Append("            ]\n");
                sb.Append("            colorMode FX_LENSFLARE_FIXED\n");
                sb.Append("            colorSeedIndex 0\n");
                sb.Append("            colorVariance 0\n");
                sb.Append("            minColorStrength ").Append(N(strength)).Append('\n');
                sb.Append("            maxColorStrength ").Append(N(strength)).Append('\n');
                sb.Append("            previousMaxColorStrength 1\n");
                sb.Append("            colorDepthEnabled 1\n");
                sb.Append("            colorDepthBeginFadeIn 0\n");
                sb.Append("            colorDepthEndFadeIn 0\n");
                sb.Append("            colorDepthBeginFadeOut 0\n");
                sb.Append("            colorDepthEndFadeOut 0\n");
                sb.Append("            colorDepthPower 1\n");
                sb.Append("            colorDepthMin 0\n");
                sb.Append("            colorDepthMax 1\n");
                Gradient("colorGradient", 0x1EC, "FX_LENSFLARE_GRADIENT_SOURCE_TO_SCREEN");
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
                sb.Append("            minYOffset 0\n");
                sb.Append("            maxYOffset 10\n");
                sb.Append("            previousMaxXOffset 10\n");
                sb.Append("            previousMinYOffset 0\n");
                sb.Append("            previousMaxYOffset 10\n");
                sb.Append("            previousYOffsetMode FX_LENSFLARE_FIXED\n");
                sb.Append("            previousYOffsetSeedIndex 1\n");
                sb.Append("            previousYOffsetVariance 0\n");
                Gradient("offsetGradient", 0xAC, "FX_LENSFLARE_GRADIENT_SOURCE_TO_SCREEN");
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
                sb.Append("            minXScale ").Append(N(minXScale)).Append('\n');
                sb.Append("            maxXScale ").Append(N(maxXScale)).Append('\n');
                sb.Append("            minYScale ").Append(N(minYScale)).Append('\n');
                sb.Append("            maxYScale ").Append(N(maxYScale)).Append('\n');
                sb.Append("            previousMaxXScale ").Append(N(maxXScale)).Append('\n');
                sb.Append("            previousMinYScale ").Append(N(minYScale)).Append('\n');
                sb.Append("            previousMaxYScale ").Append(N(maxYScale)).Append('\n');
                sb.Append("            previousYScaleMode FX_LENSFLARE_FIXED\n");
                sb.Append("            previousYScaleSeedIndex 1\n");
                sb.Append("            previousYScaleVariance 0\n");
                Gradient("scaleGradient", 0x164, "FX_LENSFLARE_GRADIENT_SOURCE_TO_SCREEN");
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
            /// Resolves a T8 GfxImage asset pointer to its name (60-bit name
            /// hash inline at image+0x20)
            /// </summary>
            private static string ImageName(HydraInstance instance, long imageAddress)
            {
                if (!IsCanonicalPointer(imageAddress))
                    return null;
                var hash = (ulong)instance.Reader.ReadInt64(imageAddress + 0x20) & HashMask;
                return hash == 0 ? null : GetHashName(hash, "image");
            }

            private static string Sanitize(string name)
            {
                var sb = new StringBuilder(name.Length);
                foreach (var ch in name)
                    sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
                return sb.ToString();
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
