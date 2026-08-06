using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HydraX.Library
{
    public partial class BlackOps4
    {
        /// <summary>
        /// Black Ops 4 FX pool (index 33) — rips compiled T8 FxEffectDefs to a
        /// faithful BO4-native text format ("bo4fx 2"), plus optional raw
        /// research dumps (RawDumps=true) used while reversing the layout.
        ///
        /// T8 FxElemDef layout (0x280) established 2026-08-04 by value voting
        /// against the 322 BO3-named anchor effects — see repo CLAUDE.md
        /// "T8 FxElemDef (0x280) map". Graphs are emitted in iwfx-style
        /// two-curve syntax so the future T8-&gt;T7 porter is mostly a key-mapper.
        /// Unmapped nonzero byte ranges are emitted as hex so nothing is lost.
        /// </summary>
        private class FXEffect : IAssetPool
        {
            /// <summary>
            /// Also write raw research dumps (.header.bin/.elems.bin/.ptrs.*)
            /// </summary>
            public static bool RawDumps = false;

            /// <summary>
            /// Bytes dumped per chased pointer target in raw mode
            /// </summary>
            public static int PointerChaseSize = 0x4000;

            /// <summary>
            /// T8 FxElemDef stride
            /// </summary>
            public const int ElemSize = 0x280;

            /// <summary>
            /// T8 FxEffectDef header offsets:
            /// +0x00 name hash, +0x10 flags, +0x14 msecLoopingLife,
            /// +0x18 msecNonLoopingLife, +0x1C gpuMsecLife, +0x20 ?,
            /// +0x24/26/28 u16 elemDefCount L/O/E, +0x30 FxElements ptr,
            /// +0x38 f32[3] boundingBoxDim, +0x44 f32[3] boundingBoxCentre
            /// </summary>
            private const int ElemCountsOffset = 0x24;
            private const int ElemsPointerOffset = 0x30;

            private const int VisRec = 0x68;      // vis sample record (BASE 0x34 + AMP 0x34)
            private const int VisHalf = 0x34;
            private const int VelRec = 0x60;      // vel sample record (T5/T7 layout)
            private const double Rad2Deg = 180.0 / Math.PI;

            private static readonly string[] ElemTypeNames =
            {
                "billboardSprite", "orientedSprite", "rotatedSprite", "tail",
                "line", "trail", "cloud", "model", "dynamicLight2", "type9",
                "type10", "lensFlare", "type12", "decal", "runner",
                "type15", "type16",
            };

            /// <summary>
            /// Short labels for the asset list's Info column (the full names
            /// don't fit) — index-parallel with ElemTypeNames.
            /// </summary>
            private static readonly string[] ElemTypeShort =
            {
                "Sprite", "Oriented", "Rotated", "Tail", "Line", "Trail",
                "Cloud", "Model", "Light", "Type9", "Type10", "Flare",
                "Type12", "Decal", "Runner", "Type15", "Type16",
            };

            /// <summary>
            /// Builds the Info string shown in the asset list: emitter counts
            /// per section plus which element types the effect is made of, so
            /// it's visible before exporting whether an effect contains the
            /// T8-only types the .efx writer has to drop (see WriteEmitterEfx).
            /// </summary>
            private static string Describe(int countL, int countO, int countE, byte[] elems)
            {
                // the header's three elemDefCounts, which are also the order the
                // element array is partitioned in; empty sections are dropped
                var sb = new StringBuilder();
                if (countL > 0) sb.Append(countL).Append(" Looping");
                if (countO > 0) sb.Append(sb.Length > 0 ? " / " : "").Append(countO).Append(" OneShot");
                if (countE > 0) sb.Append(sb.Length > 0 ? " / " : "").Append(countE).Append(" Emission");
                if (sb.Length == 0) sb.Append("Empty");

                if (elems == null)
                    return sb.ToString();

                // count each type, splitting off the ones the .efx writer skips
                // (same predicate WriteEmitterEfx uses, off the same array)
                var counts = new SortedDictionary<int, int>();
                int skipped = 0;
                for (int i = 0; (i + 1) * ElemSize <= elems.Length; i++)
                {
                    int t = elems[i * ElemSize + 0x264];
                    if (t >= ElemTypeNames.Length || ElemTypeNames[t].StartsWith("type"))
                    {
                        skipped++;
                        continue;
                    }
                    counts.TryGetValue(t, out int n);
                    counts[t] = n + 1;
                }

                var named = new List<KeyValuePair<int, int>>(counts);
                named.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));

                for (int i = 0; i < named.Count; i++)
                {
                    sb.Append(i == 0 ? ": " : ", ").Append(ElemTypeShort[named[i].Key]);
                    if (named[i].Value > 1)
                        sb.Append(" x").Append(named[i].Value);
                }
                if (skipped > 0)
                    sb.Append(named.Count > 0 ? ", " : ": ").Append(skipped).Append(" Skipped");

                return sb.ToString();
            }

            #region Graph
            /// <summary>
            /// A two-curve iwfx-style graph (ported from the BO3 decompiler)
            /// </summary>
            private class Graph
            {
                public double Scale;
                public List<double[]> CurveA = new List<double[]>();
                public List<double[]> CurveB = new List<double[]>();

                public static Graph Flat(double scale, double a = 1.0, double? b = null)
                {
                    var g = new Graph { Scale = scale };
                    g.CurveA.Add(new[] { 0.0, a }); g.CurveA.Add(new[] { 1.0, a });
                    g.CurveB.Add(new[] { 0.0, b ?? a }); g.CurveB.Add(new[] { 1.0, b ?? a });
                    return g;
                }

                public static Graph Factor(double[] times, double[][] a, double[][] b)
                {
                    double peak = 0;
                    foreach (var curve in new[] { a, b })
                        foreach (var kf in curve)
                            foreach (var v in kf)
                                peak = Math.Max(peak, Math.Abs(v));

                    var g = new Graph { Scale = peak < 1e-12 ? 0 : peak };
                    double sc = peak < 1e-12 ? 1 : peak;
                    for (int c = 0; c < 2; c++)
                    {
                        var src = c == 0 ? a : b;
                        var dst = c == 0 ? g.CurveA : g.CurveB;
                        for (int s = 0; s < times.Length; s++)
                        {
                            var kf = new double[1 + src[s].Length];
                            kf[0] = times[s];
                            for (int v = 0; v < src[s].Length; v++)
                                kf[1 + v] = src[s][v] / sc;
                            dst.Add(kf);
                        }
                        if (times.Length == 1)
                        {
                            var kf = (double[])dst[0].Clone();
                            kf[0] = 1.0;
                            dst.Add(kf);
                        }
                    }
                    return g;
                }

                /// <summary>
                /// True when curve B differs from curve A — the compiler drops B
                /// unless the emitter had the matching useRand* editor flag, so
                /// this recovers those flags on the way back out.
                /// </summary>
                public bool Differs()
                {
                    if (CurveA.Count != CurveB.Count)
                        return true;
                    for (int i = 0; i < CurveA.Count; i++)
                        for (int v = 1; v < CurveA[i].Length; v++)
                            if (Math.Abs(CurveA[i][v] - CurveB[i][v]) > 1e-9)
                                return true;
                    return false;
                }

                public void Write(StringBuilder sb, string key)
                {
                    sb.Append('\t').Append(key).Append(' ').Append(N(Scale)).Append('\n');
                    sb.Append("\t{\n");
                    foreach (var curve in new[] { CurveA, CurveB })
                    {
                        sb.Append("\t\t{\n");
                        foreach (var kf in curve)
                        {
                            sb.Append("\t\t\t");
                            for (int i = 0; i < kf.Length; i++)
                            {
                                if (i > 0) sb.Append(' ');
                                sb.Append(N(kf[i]));
                            }
                            sb.Append('\n');
                        }
                        sb.Append("\t\t}\n");
                    }
                    sb.Append("\t};\n");
                }
            }
            #endregion

            /// <summary>
            /// Formats a number the way the effect editor does: whole numbers
            /// plain, otherwise six significant digits with no trailing zeros and
            /// never exponent notation. The rounding matters — compiled float32s
            /// otherwise decompile to noise ("-360.00001" for a source -360).
            /// </summary>
            private static string N(double v)
            {
                if (double.IsNaN(v) || double.IsInfinity(v))
                    return "0";
                if (v != 0 && Math.Abs(v) < 1e15)
                {
                    int digits = 5 - (int)Math.Floor(Math.Log10(Math.Abs(v)));
                    if (digits >= 0 && digits <= 15)
                        v = Math.Round(v, digits);
                }
                if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15)
                    return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
                return v.ToString("0.###############", CultureInfo.InvariantCulture);
            }

            private static string Pair(double a, double b) => N(a) + " " + N(b);

            public int AssetSize { get; set; }
            public int AssetCount { get; set; }
            public long StartAddress { get; set; }
            public long EndAddress { get { return StartAddress + (AssetCount * AssetSize); } set => throw new NotImplementedException(); }
            public string Name => "fx";
            public int Index => (int)AssetPool.fx;

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
                    var first = instance.Reader.ReadInt64(address);

                    if (IsNullAsset(first))
                        continue;

                    var hash = (ulong)first & HashMask;

                    var hdr = ReadChunked(instance, address, AssetSize);
                    int countL = BitConverter.ToUInt16(hdr, ElemCountsOffset);
                    int countO = BitConverter.ToUInt16(hdr, ElemCountsOffset + 2);
                    int countE = BitConverter.ToUInt16(hdr, ElemCountsOffset + 4);
                    int total = countL + countO + countE;
                    long elemsPtr = BitConverter.ToInt64(hdr, ElemsPointerOffset);

                    // one bulk read per effect: only the elemType byte at
                    // +0x264 of each record is wanted, but a read per elem
                    // costs far more in syscalls than it saves in bytes
                    byte[] elems = null;
                    if (IsCanonicalPointer(elemsPtr) && total > 0 && total < 0x800)
                        elems = ReadChunked(instance, elemsPtr, total * ElemSize);

                    results.Add(new Asset()
                    {
                        Name        = GetHashName(hash, "fx"),
                        Type        = Name,
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                        // Zone comes from where the elements were streamed to,
                        // not the header (pool slots sit outside every zone)
                        Zone        = ((BlackOps4)instance.Game).GetZoneName(elemsPtr),
                        Information = Describe(countL, countO, countE, elems)
                    });
                }

                return results;
            }

            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var address = (long)asset.Data;
                var header = ReadChunked(instance, address, AssetSize);

                int total = BitConverter.ToUInt16(header, ElemCountsOffset)
                          + BitConverter.ToUInt16(header, ElemCountsOffset + 2)
                          + BitConverter.ToUInt16(header, ElemCountsOffset + 4);
                long elemsPtr = BitConverter.ToInt64(header, ElemsPointerOffset);

                byte[] elems = null;
                if (IsCanonicalPointer(elemsPtr) && total > 0 && total < 0x800)
                    elems = ReadChunked(instance, elemsPtr, total * ElemSize);

                var name = asset.Name.TrimStart('/');
                var leaf = name.Substring(name.LastIndexOf('/') + 1);

                // flat export: the BO3 source, the BO4-native rip and the
                // asset list land directly in the game folder
                var assets = new SortedSet<string>(StringComparer.Ordinal);
                var efx = DecompileEfx(header, elems, instance, assets);

                var dir = Path.Combine("exported_files", instance.Game.Name);

                Write(Path.Combine(dir, leaf + ".efx"), efx);

                if (instance.Settings["ExportBO4FX", "No"] == "Yes")
                    Write(Path.Combine(dir, leaf + ".bo4fx"), Decompile(asset.Name, header, elems, instance));

                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, leaf + "_assets.txt"), AssetList(name, assets));
                }

                if (RawDumps)
                {
                    string rawDir = Path.Combine("exported_files", instance.Game.Name, "fx_raw");
                    Directory.CreateDirectory(rawDir);
                    string baseName = Path.Combine(rawDir, asset.Name.Replace('/', '_').Replace('\\', '_'));
                    File.WriteAllBytes(baseName + ".header.bin", header);
                    File.WriteAllText(baseName + ".info.txt", AnnotateHeader(address, header));
                    if (elems != null)
                        File.WriteAllBytes(baseName + ".elems.bin", elems);
                    DumpPointerTargets(instance, baseName, header, elems);
                }
            }

            private static void Write(string path, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text.Replace("\n", "\r\n"));   // Radiant needs CRLF
            }

            #region BO3 source writer (iwfx 3)
            /// <summary>
            /// Ports a compiled T8 effect to BO3 iwfx 3 source. The key set and
            /// order are the shipping ones (same as the BO3 decompiler writes).
            /// T8 fields with no T7 equivalent are dropped; T7 keys T8 doesn't
            /// store are written at their editor defaults. Element types with no
            /// T7 counterpart (9, 10, 12, 15, 16) are skipped.
            /// </summary>
            private string DecompileEfx(byte[] hdr, byte[] elems, HydraInstance instance, SortedSet<string> assets)
            {
                var sb = new StringBuilder();

                int countL = BitConverter.ToUInt16(hdr, ElemCountsOffset);
                int countO = BitConverter.ToUInt16(hdr, ElemCountsOffset + 2);
                int countE = BitConverter.ToUInt16(hdr, ElemCountsOffset + 4);

                double dimX = F(hdr, 0x38), dimY = F(hdr, 0x3C), dimZ = F(hdr, 0x40);
                double cenX = F(hdr, 0x44), cenY = F(hdr, 0x48), cenZ = F(hdr, 0x4C);
                bool hasBox = dimX != 0 || dimY != 0 || dimZ != 0;

                sb.Append("iwfx 3\n\n");
                sb.Append("\teditorFlags;\n");
                sb.Append("\tefFlags").Append(hasBox ? " efUseBoundingBox" : "").Append(";\n");
                sb.Append("\tefPriority 45;\n");   // not stored in T8 — editor default
                sb.AppendFormat("\tefBoundingBoxMin {0} {1} {2};\n", N(cenX - dimX), N(cenY - dimY), N(cenZ - dimZ));
                sb.AppendFormat("\tefBoundingBoxMax {0} {1} {2};\n", N(cenX + dimX), N(cenY + dimY), N(cenZ + dimZ));
                sb.Append("\tocclusionQueryDepthBias 0;\n");
                sb.Append("\tocclusionQueryFadeIn 0;\n");
                sb.Append("\tocclusionQueryFadeOut 0;\n");
                sb.Append("\tocclusionQueryScaleRange 1 0;\n");
                sb.Append("\tnormalsShape 0;\n\tnormalsShapeOffset 0 0 0;\n\tnormalsShapeRadius 0;\n");
                sb.Append("\tnormalsShapeLength 0;\n\tnormalsShapeAngles 0 0 0;\n");
                sb.Append("\tnormalsShapeVisualizationColor 0 0 0 0;\n\tefCompletion 0;\n");
                sb.Append("\tlodDefault0 0 0;\n\tlodDefault1 0 0;\n\tlodDefault2 0 0;\n\tlodDefault3 0 0;\n");

                int total = elems == null ? 0 : countL + countO + countE;
                var counters = new Dictionary<string, int>();

                for (int i = 0; i < total; i++)
                {
                    if ((i + 1) * ElemSize > elems.Length)
                        break;
                    var c = new byte[ElemSize];
                    Buffer.BlockCopy(elems, i * ElemSize, c, 0, ElemSize);
                    bool looping = i < countL;
                    string section = looping ? "looping" : (i < countL + countO ? "oneshot" : "emission");
                    WriteEmitterEfx(sb, c, looping, section, counters, instance, assets);
                }

                return sb.ToString();
            }

            private void WriteEmitterEfx(StringBuilder sb, byte[] c, bool looping, string section,
                                         Dictionary<string, int> counters, HydraInstance instance,
                                         SortedSet<string> assets)
            {
                int elemType = c[0x264];
                if (elemType >= ElemTypeNames.Length || ElemTypeNames[elemType].StartsWith("type"))
                    return;   // T8-only element type, nothing to port it to

                double F0(int off) => F(c, off);
                int I(int off) => BitConverter.ToInt32(c, off);
                string FPair(int off) => Pair(F(c, off), F(c, off + 4));
                string IPair(int off) => I(off) + " " + I(off + 4);
                string DegPair(int off) => Pair(F(c, off) * Rad2Deg, F(c, off + 4) * Rad2Deg);

                string typeName = ElemTypeNames[elemType];
                counters.TryGetValue(section + typeName, out int n);
                counters[section + typeName] = n + 1;

                int visualCount = c[0x26E];
                int velN = c[0x26F];
                int visN = c[0x273];

                // ---- graphs from the sample arrays ----
                Graph[] vel0 = null, vel1 = null;
                Graph rotG = null, size0 = null, size1 = null, scaleG = null,
                      colorG = null, alphaG = null, lightI = null, lightR = null, lightF = null;

                long velPtr = BitConverter.ToInt64(c, 0x88);
                if (IsCanonicalPointer(velPtr))
                {
                    var vel = ReadChunked(instance, velPtr, (velN + 1) * VelRec);
                    var times = SampleTimes(velN);
                    double vmul = 1000.0 * Math.Max(velN, 1);
                    var graphs = new Graph[2][];
                    for (int half = 0; half < 2; half++)
                    {
                        graphs[half] = new Graph[3];
                        for (int ax = 0; ax < 3; ax++)
                        {
                            var a = new double[velN + 1][];
                            var b = new double[velN + 1][];
                            for (int s = 0; s <= velN; s++)
                            {
                                int o = s * VelRec + half * 0x30;
                                double bas = BitConverter.ToSingle(vel, o + ax * 4) * vmul;
                                double amp = BitConverter.ToSingle(vel, o + 12 + ax * 4) * vmul;
                                a[s] = new[] { bas };
                                b[s] = new[] { bas + amp };
                            }
                            graphs[half][ax] = Graph.Factor(times, a, b);
                        }
                    }
                    vel0 = graphs[0];
                    vel1 = graphs[1];
                }

                long visPtr = BitConverter.ToInt64(c, 0x98);
                if (IsCanonicalPointer(visPtr))
                {
                    var vis = ReadChunked(instance, visPtr, (visN + 1) * VisRec);
                    var times = SampleTimes(visN);
                    double rmul = 1000.0 * Math.Max(visN, 1) * Rad2Deg;

                    double[][] Rgb(int off)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                        {
                            int o = s * VisRec + off;
                            vals[s] = new[] { vis[o] / 255.0, vis[o + 1] / 255.0, vis[o + 2] / 255.0 };
                        }
                        return vals;
                    }
                    double[][] AlphaBytes(int off)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                            vals[s] = new[] { vis[s * VisRec + off] / 255.0 };
                        return vals;
                    }
                    double[][] BaseF(int off, double mul)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                            vals[s] = new[] { BitConverter.ToSingle(vis, s * VisRec + off) * mul };
                        return vals;
                    }
                    // AMP half stores a delta for the float fields (absolute for colour)
                    double[][] Amp(int off, double mul)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                            vals[s] = new[] { (BitConverter.ToSingle(vis, s * VisRec + off)
                                             + BitConverter.ToSingle(vis, s * VisRec + off + VisHalf)) * mul };
                        return vals;
                    }

                    colorG = Graph.Factor(times, Rgb(0x00), Rgb(VisHalf));
                    if (colorG.Scale > 0) NormalizeColorScale(colorG);
                    alphaG = Graph.Factor(times, AlphaBytes(0x03), AlphaBytes(VisHalf + 0x03));
                    rotG = Graph.Factor(times, BaseF(0x04, rmul), Amp(0x04, rmul));
                    size0 = Graph.Factor(times, BaseF(0x0C, 2), Amp(0x0C, 2));
                    size1 = Graph.Factor(times, BaseF(0x10, 2), Amp(0x10, 2));
                    scaleG = Graph.Factor(times, BaseF(0x14, 1), Amp(0x14, 1));
                    if (elemType == 8)
                    {
                        lightI = Graph.Factor(times, BaseF(0x20, 1), Amp(0x20, 1));
                        lightR = Graph.Factor(times, BaseF(0x28, 1), Amp(0x28, 1));
                        lightF = Graph.Factor(times, BaseF(0x30, 1), Amp(0x30, 1));
                    }
                }

                var zero3 = new[] { Graph.Flat(0, 0), Graph.Flat(0, 0), Graph.Flat(0, 0) };
                vel0 = vel0 ?? zero3;
                vel1 = vel1 ?? zero3;
                rotG = rotG ?? Graph.Flat(0, 0);
                size0 = size0 ?? Graph.Flat(1);
                size1 = size1 ?? Graph.Flat(0, 0);
                scaleG = scaleG ?? Graph.Flat(0, 0);
                colorG = colorG ?? Graph.Flat(1);
                alphaG = alphaG ?? Graph.Flat(1);
                lightI = lightI ?? Graph.Flat(10000);
                lightR = lightR ?? Graph.Flat(100);
                lightF = lightF ?? Graph.Flat(90);

                // ---- editor flags: looping from the partition, useRand* from B != A ----
                var editorFlags = new List<string>();
                if (looping) editorFlags.Add("looping");
                bool velDiff0 = false, velDiff1 = false;
                foreach (var g in vel0) velDiff0 |= g.Differs();
                foreach (var g in vel1) velDiff1 |= g.Differs();
                if (velDiff0) editorFlags.Add("useRandVel0");
                if (velDiff1) editorFlags.Add("useRandVel1");
                if (rotG.Differs()) editorFlags.Add("useRandRotDelta");
                if (size0.Differs()) editorFlags.Add("useRandSize0");
                if (size1.Differs()) editorFlags.Add("useRandSize1");
                if (scaleG.Differs()) editorFlags.Add("useRandScale");
                if (colorG.Differs()) editorFlags.Add("useRandColor");
                if (alphaG.Differs()) editorFlags.Add("useRandAlpha");

                // ---- flags (T8 dword at +0x118 / extra at +0x11C) ----
                uint flagBits = BitConverter.ToUInt32(c, 0x118);
                uint extraBits = BitConverter.ToUInt32(c, 0x11C);
                var flags = new List<string>();
                if ((flagBits >> 1 & 1) != 0) flags.Add("spawnRelative");
                if ((flagBits >> 2 & 1) != 0) flags.Add("spawnFrustumCull");
                flags.Add(new[] { "spawnOffsetNone", "spawnOffsetSphere", "spawnOffsetCylinder", "spawnOffsetNone" }
                    [(int)(flagBits >> 4 & 3)]);
                flags.Add((flagBits >> 8 & 1) != 0 ? "runRelToOffsetEffectNow"
                    : new[] { "runRelToWorld", "runRelToSpawn", "runRelToEffect", "runRelToOffset" }
                        [(int)(flagBits >> 6 & 3)]);
                if ((flagBits >> 9 & 1) != 0) flags.Add("useCollision");
                if ((flagBits >> 10 & 1) != 0) flags.Add("dieOnTouch");
                if ((flagBits >> 11 & 1) != 0) flags.Add("drawPastFog");
                if ((flagBits >> 12 & 1) != 0) flags.Add("drawWithViewModel");

                var extraFlags = new List<string>();
                if ((extraBits & 1) != 0) extraFlags.Add("distribX");
                if ((extraBits >> 1 & 1) != 0) extraFlags.Add("distribY");
                if ((extraBits >> 2 & 1) != 0) extraFlags.Add("distribZ");
                if ((extraBits >> 3 & 1) != 0) extraFlags.Add("teamFriendly");

                // ---- atlas ----
                int ab = c[0x265];
                var atlas = new List<string> { new[] { "startFixed", "startRandom", "startIndexed", "startFixedRange" }[ab & 3] };
                if ((ab & 0x04) != 0) atlas.Add("playOverLife");
                if ((ab & 0x08) != 0) atlas.Add("loopOnlyNTimes");
                if ((ab & 0x10) != 0) atlas.Add("lerpFrames");
                int atlasBits = c[0x269] + c[0x26A];

                // ---- references ----
                string RefName(int off)
                {
                    long ptr = BitConverter.ToInt64(c, off);
                    return IsCanonicalPointer(ptr) ? ResolveAsset(ptr, instance) : "";
                }
                string SoundName(int off)
                {
                    ulong raw = BitConverter.ToUInt64(c, off);
                    return raw == 0 ? "" : GetHashName(raw & HashMask, "sound");
                }
                string fxOnImpact = RefName(0xC8);
                string fxOnDeath = RefName(0xD8);
                string emission = RefName(0xE8);
                string attachment = RefName(0xF8);
                string spawnSound = SoundName(0xB0);
                string followSound = SoundName(0xB8);

                // ---- visuals: strip the compile-time category prefixes ("ei/"
                // sprites, "el/" trails, "ec/" clouds) and the auto-generated
                // "vd/"/"vdd/" decal LOD duplicates, as the T7 decompiler does.
                // computeVisuals (+0x40) is a "|dup" twin of the same material and
                // has no T7 equivalent — dropping it is lossless.
                var visuals = new List<string>();
                if (elemType == 11)
                {
                    // lensFlare doesn't reference a material: the 16 bytes at
                    // +0x00 are the flare def's GUID inline (little-endian GUID
                    // struct) — the "uuid" key of the .klf source. 97/97 verified
                    // against the T8 klf pool (asset name = fnv1a60(uuid)).
                    var guid = new byte[16];
                    Buffer.BlockCopy(c, 0x00, guid, 0, 16);
                    visuals.Add(new Guid(guid).ToString());
                }
                else if (elemType != 8)
                {
                    var raw = ReadVisuals(c, visualCount, elemType, instance);
                    if (elemType == 7 || elemType == 14)
                    {
                        foreach (var v in raw)
                            if (v != "") visuals.Add(v);
                    }
                    else
                    {
                        foreach (var v in raw)
                        {
                            if (v == "") continue;
                            int slash = v.IndexOf('/');
                            if (slash < 0) { visuals.Add(v); continue; }
                            var prefix = v.Substring(0, slash);
                            var bare = v.Substring(slash + 1);
                            if ((prefix == "vd" || prefix == "vdd") && (raw.Contains(bare) || visuals.Contains(bare)))
                                continue;
                            visuals.Add(bare);
                        }
                    }
                }
                if (visuals.Count == 0)
                    visuals.Add("");

                // ---- record what this emitter references ----
                string visualKind = elemType == 7 ? "xmodel" : elemType == 14 ? "fx" : elemType == 11 ? "lensflare" : "material";
                foreach (var v in visuals)
                    if (v != "")
                        assets.Add(visualKind + " " + v);
                foreach (var r in new[] { fxOnImpact, fxOnDeath, emission, attachment })
                    if (r != "")
                        assets.Add("fx " + r);

                // ---- write the emitter block (canonical v3 key order) ----
                sb.Append("{\n");
                void KV(string k, string v) => sb.Append('\t').Append(k).Append(' ').Append(v).Append(";\n");
                void FlagLine(string k, List<string> f) =>
                    sb.Append('\t').Append(k).Append(f.Count > 0 ? " " + string.Join(" ", f) : "").Append(";\n");

                KV("name", string.Format("\"{0}_{1}_{2}\"", section, typeName, n));
                FlagLine("editorFlags", editorFlags);
                FlagLine("flags", flags);
                FlagLine("extraFlags", extraFlags);
                KV("spawnRange", FPair(0x130));
                KV("fadeInRange", FPair(0x138));
                KV("fadeOutRange", FPair(0x140));
                KV("spawnFrustumCullRadius", N(F0(0x234)));
                int spawnBase = I(0x108), spawnRand = I(0x10C);
                if (looping)
                {
                    if (spawnRand == int.MaxValue) spawnRand = 0;   // rand 0 compiles to INT_MAX
                    KV("spawnLooping", spawnBase + " " + spawnRand);
                    KV("spawnLoopingSpawnCount", IPair(0x110));
                    KV("spawnOneShot", spawnBase + " 0");           // editor mirrors the interval
                }
                else
                {
                    KV("spawnLooping", "200 0");
                    KV("spawnLoopingSpawnCount", IPair(0x110));
                    KV("spawnOneShot", spawnBase + " " + spawnRand);
                }
                KV("spawnDelayMsec", IPair(0x120));
                KV("lifeSpanMsec", IPair(0x128));
                KV("spawnOrgX", FPair(0x148));
                KV("spawnOrgY", FPair(0x150));
                KV("spawnOrgZ", FPair(0x158));
                KV("spawnOffsetRadius", FPair(0x160));
                KV("spawnOffsetHeight", FPair(0x168));
                KV("spawnOffsetCylindricalAxis", "0");
                KV("spawnAnglePitch", DegPair(0x170));
                KV("spawnAngleYaw", DegPair(0x178));
                KV("spawnAngleRoll", DegPair(0x180));
                KV("angleVelPitch", Pair(F0(0x188) * Rad2Deg * 1000, F0(0x18C) * Rad2Deg * 1000));
                KV("angleVelYaw", Pair(F0(0x190) * Rad2Deg * 1000, F0(0x194) * Rad2Deg * 1000));
                KV("angleVelRoll", Pair(F0(0x198) * Rad2Deg * 1000, F0(0x19C) * Rad2Deg * 1000));
                KV("initialRot", DegPair(0x1A0));
                KV("rotationAxis", "0 0 0 1");
                KV("gravity", Pair(F0(0x1A8) * 100, F0(0x1AC) * 100));
                KV("elasticity", FPair(0x1B0));
                KV("windinfluence", "0");
                FlagLine("atlasBehavior", atlas);
                KV("atlasIndex", c[0x266].ToString());
                KV("atlasFps", c[0x267].ToString());
                KV("atlasLoopCount", c[0x268].ToString());
                KV("atlasColIndexBits", c[0x269].ToString());
                KV("atlasRowIndexBits", c[0x26A].ToString());
                KV("atlasEntryCount", (atlasBits > 0 ? 1 << atlasBits : 0).ToString());  // not stored; derived
                KV("atlasIndexRange", c[0x26B].ToString());
                for (int ax = 0; ax < 3; ax++) vel0[ax].Write(sb, "velGraph0" + "XYZ"[ax]);
                for (int ax = 0; ax < 3; ax++) vel1[ax].Write(sb, "velGraph1" + "XYZ"[ax]);
                rotG.Write(sb, "rotGraph");
                size0.Write(sb, "sizeGraph0");
                size1.Write(sb, "sizeGraph1");
                scaleG.Write(sb, "scaleGraph");
                Graph.Flat(1).Write(sb, "childSizeScaleGraph");    // T8 offset unknown
                colorG.Write(sb, "colorGraph");
                alphaG.Write(sb, "alphaGraph");
                lightI.Write(sb, "lightIntensityGraph");
                lightR.Write(sb, "lightRadiusGraph");
                lightF.Write(sb, "lightFovGraph");
                Graph.Flat(1).Write(sb, "inheritParentMovementGraph");
                Graph.Flat(1).Write(sb, "attractorGraph");
                KV("attractorLocalPosition", "0 0 0");
                KV("lightingFrac", N(c[0x275] / 255.0));
                KV("collOffset", "0 0 0");
                KV("collRadius", "0");
                KV("fxOnImpact", string.Format("\"{0}\"", fxOnImpact));
                KV("fxOnDeath", string.Format("\"{0}\"", fxOnDeath));
                KV("displacement", c[0x274].ToString());
                KV("emission", string.Format("\"{0}\"", emission));
                KV("emitDist", FPair(0x1B8));
                KV("emitDistVariance", FPair(0x1C0));
                KV("emitDensity", "1 0");
                KV("emitSizeForDensity", "1");
                KV("attachment", string.Format("\"{0}\"", attachment));
                KV("attachmentDensity", "1 0");
                KV("attachmentSizeForDensity", "1");
                KV("trailSplitDist", "0");      // T8 trail struct (+0x80) not decoded
                KV("trailScrollTime", "0");
                KV("trailRepeatDist", "0");
                KV("trailFadeInDist", "0");
                KV("trailFadeOutDist", "0");
                KV("alphafadetimemsec", I(0x25C).ToString());
                KV("maxwind_mag", "0");
                KV("maxwind_life", "0");
                KV("maxwind_interval", "1");
                sb.Append("\telemSpawnSound\n\t{\n");
                if (spawnSound != "") sb.Append("\t\t\"").Append(spawnSound).Append("\"\n");
                sb.Append("\t};\n");
                sb.Append("\telemFollowSound\n\t{\n");
                if (followSound != "") sb.Append("\t\t\"").Append(followSound).Append("\"\n");
                sb.Append("\t};\n");
                KV("cloudDensity", "1024 0");   // not mapped in T8
                KV("spotLightFovInnerFraction", "0.5");
                KV("spotLightStartRadius", "36");
                KV("spotLightEndRadius", "196");
                KV("alphaDissolve", N(F0(0x21C)));
                KV("zFeather", N(F0(0x220)));
                KV("falloffBeginAngle", I(0x228).ToString());
                KV("falloffEndAngle", I(0x22C).ToString());
                KV("lfSourceDir", "1 0 0");
                KV("lfSourceSize", "15");
                KV("billboardPivot", Pair(F0(0x214) / 2, -F0(0x218) / 2));
                KV("levelOfDetail", "0");
                sb.Append('\t').Append(typeName).Append("\n\t{\n");
                if (elemType == 8)
                    WriteDefaultLightDef(sb, section + n);   // embedded lightdefs don't survive compilation
                else
                    foreach (var v in visuals)
                        sb.Append("\t\t\"").Append(v).Append("\"\n");
                sb.Append("\t};\n");
                sb.Append("}\n");
            }

            /// <summary>
            /// Renders the per-effect assets.txt: every material, model and
            /// effect the emitters reference, one "kind,name" per line.
            /// Unresolved names are written as the bare hash (no placeholder prefix).
            /// </summary>
            internal static string AssetList(string effectName, SortedSet<string> assets)
            {
                var sb = new StringBuilder();
                sb.Append("# assets referenced by ").Append(effectName).Append("\r\n");
                foreach (var entry in assets)
                {
                    int space = entry.IndexOf(' ');
                    if (space < 0)
                        sb.Append(entry).Append("\r\n");
                    else
                        sb.Append(entry, 0, space).Append(',')
                          .Append(StripHashPrefix(entry.Substring(space + 1))).Append("\r\n");
                }
                return sb.ToString();
            }

            /// <summary>
            /// Unresolved names are placeholders of the form "&lt;prefix&gt;_&lt;hash hex&gt;" —
            /// the assets file lists the bare hash instead (the kind column says the rest).
            /// </summary>
            private static string StripHashPrefix(string name)
            {
                int us = name.IndexOf('_');
                if (us < 0)
                    return name;
                switch (name.Substring(0, us))
                {
                    case "hash":
                    case "fx":
                    case "sound":
                    case "image":
                    case "klf":
                    case "xasset":
                        break;
                    default:
                        return name;
                }
                string rest = name.Substring(us + 1);
                if (rest.Length < 8)
                    return name;
                foreach (char c in rest)
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                        return name;
                return rest;
            }

            /// <summary>
            /// colorGraph conventionally uses scale 1 with 0-1 channel values
            /// </summary>
            private static void NormalizeColorScale(Graph g)
            {
                foreach (var curve in new[] { g.CurveA, g.CurveB })
                    foreach (var kf in curve)
                        for (int i = 1; i < kf.Length; i++)
                            kf[i] *= g.Scale;
                g.Scale = 1.0;
            }

            /// <summary>
            /// Default OMNI lightdef for dynamicLight2 emitters. The real embedded
            /// Radiant lightdef doesn't survive compilation in either game, and an
            /// empty "" block here derails Radiant's parser for the whole file. The
            /// light's actual behaviour is in the lightIntensity/Radius/Fov graphs.
            /// </summary>
            private static void WriteDefaultLightDef(StringBuilder sb, string seed)
            {
                uint hash = 2166136261;
                foreach (char ch in seed)
                    hash = (hash ^ ch) * 16777619;
                sb.Append("\t\tnew").Append(hash & 0x7FFFFFFF).Append("\n\t\t{\n");
                string[] lines =
                {
                    "ENABLE_FALLOFF 1",
                    "PRIMARY_NOSHADOWMAP 1",
                    "PRIMARY_TYPE OMNI",
                    "_color 1.000000 1.000000 1.000000",
                    "angles 0.000000 0.000000 0.000000",
                    "bake_intensity_scale 1.000000",
                    "bulbLength 0.000000",
                    "culling_cutoff 0.000000",
                    "culling_falloff 0.000000",
                    "cut_on 0.000000",
                    "falloffdistance 10.000000",
                    "far_edge 0.500000",
                    "fov_outer 0.500458",
                    "min_light_cutoff 0.000000",
                    "near_edge 0.000000",
                    "penumbraRadius 3.000000",
                    "radius 100.000000",
                    "roundness 0.500000",
                    "culling_use_pure_radius 0",
                    "PROBE_ONLY 0",
                    "def_angle 0.000000",
                    "def_center 0.000000 0.000000",
                    "def_offset 0.000000 0.000000",
                    "def_rotation 0.000000",
                    "def_scroll 0.000000 0.000000",
                    "def_shear 0.000000 0.000000",
                    "def_tile 1.000000 1.000000",
                    "shadowUpdate Never",
                    "shadowmapScale 1",
                    "spec_comp 0.000000",
                    "superellipse 0.750000 1.000000 0.750000 1.000000",
                    "ortho_effect 0.000000",
                    "scriptable 0",
                    "stops 11",
                    "ortho_effect 0.000000",
                    "volumetric 0",
                    "volumetricCookies 0",
                    "volumetricIntensityBoost 0.000000",
                    "volumetricSampleCount 8",
                };
                foreach (var line in lines)
                    sb.Append("\t\t\t").Append(line).Append('\n');
                sb.Append("\t\t};\n");
            }
            #endregion

            #region Decompiler
            private string Decompile(string name, byte[] hdr, byte[] elems, HydraInstance instance)
            {
                var sb = new StringBuilder();
                ulong hash = BitConverter.ToUInt64(hdr, 0) & HashMask;
                int countL = BitConverter.ToUInt16(hdr, 0x24);
                int countO = BitConverter.ToUInt16(hdr, 0x26);
                int countE = BitConverter.ToUInt16(hdr, 0x28);

                sb.Append("bo4fx 2\n");
                sb.Append("name \"").Append(name).Append("\";\n");
                sb.AppendFormat("nameHash 0x{0:X15};\n", hash);
                sb.AppendFormat("flags 0x{0:X8};\n", BitConverter.ToUInt32(hdr, 0x10));
                sb.Append("msecLoopingLife ").Append(BitConverter.ToInt32(hdr, 0x14)).Append(";\n");
                sb.Append("msecNonLoopingLife ").Append(BitConverter.ToInt32(hdr, 0x18)).Append(";\n");
                sb.Append("gpuMsecLife ").Append(BitConverter.ToInt32(hdr, 0x1C)).Append(";\n");
                sb.Append("unknown20 ").Append(BitConverter.ToInt32(hdr, 0x20)).Append(";\n");
                sb.AppendFormat("elemDefCounts {0} {1} {2};\n", countL, countO, countE);
                sb.AppendFormat("boundingBoxDim {0} {1} {2};\n",
                    N(F(hdr, 0x38)), N(F(hdr, 0x3C)), N(F(hdr, 0x40)));
                sb.AppendFormat("boundingBoxCentre {0} {1} {2};\n",
                    N(F(hdr, 0x44)), N(F(hdr, 0x48)), N(F(hdr, 0x4C)));
                HexIfNonZero(sb, "", hdr, 0x50, AssetSize - 0x50, "hdrUnk");

                int total = countL + countO + countE;
                if (elems == null)
                    total = 0;

                for (int i = 0; i < total; i++)
                {
                    var c = new byte[ElemSize];
                    if ((i + 1) * ElemSize > elems.Length)
                        break;
                    Buffer.BlockCopy(elems, i * ElemSize, c, 0, ElemSize);
                    string section = i < countL ? "looping" : (i < countL + countO ? "oneshot" : "emission");
                    WriteElement(sb, i, section, c, instance);
                }

                return sb.ToString();
            }

            private void WriteElement(StringBuilder sb, int index, string section, byte[] c, HydraInstance instance)
            {
                double F0(int off) => F(c, off);
                int I(int off) => BitConverter.ToInt32(c, off);
                string FPair(int off) => Pair(F(c, off), F(c, off + 4));
                string IPair(int off) => I(off) + " " + I(off + 4);
                string DegPair(int off) => Pair(F(c, off) * Rad2Deg, F(c, off + 4) * Rad2Deg);
                string DegMsPair(int off) => Pair(F(c, off) * Rad2Deg * 1000, F(c, off + 4) * Rad2Deg * 1000);

                int elemType = c[0x264];
                string typeName = elemType < ElemTypeNames.Length ? ElemTypeNames[elemType] : "type" + elemType;
                bool looping = (c[0x260] & 1) != 0;
                int visualCount = c[0x26E];
                int velN = c[0x26F];
                int visN = c[0x273];

                sb.Append("\nelement ").Append(index).Append(" {\n");
                void KV(string k, string v) => sb.Append('\t').Append(k).Append(' ').Append(v).Append(";\n");

                KV("section", section);
                KV("type", typeName);
                KV("looping", looping ? "1" : "0");

                // ---- flags (decoded + raw) ----
                uint flags = BitConverter.ToUInt32(c, 0x118);
                uint extra = BitConverter.ToUInt32(c, 0x11C);
                var toks = new List<string>();
                if ((flags >> 1 & 1) != 0) toks.Add("spawnRelative");
                if ((flags >> 2 & 1) != 0) toks.Add("spawnFrustumCull");
                toks.Add(new[] { "spawnOffsetNone", "spawnOffsetSphere", "spawnOffsetCylinder", "spawnOffset3" }[(int)(flags >> 4 & 3)]);
                toks.Add((flags >> 8 & 1) != 0 ? "runRelToOffsetEffectNow"
                    : new[] { "runRelToWorld", "runRelToSpawn", "runRelToEffect", "runRelToOffset" }[(int)(flags >> 6 & 3)]);
                if ((flags >> 9 & 1) != 0) toks.Add("useCollision");
                if ((flags >> 10 & 1) != 0) toks.Add("dieOnTouch");
                if ((flags >> 11 & 1) != 0) toks.Add("drawPastFog");
                if ((flags >> 12 & 1) != 0) toks.Add("drawWithViewModel");
                var extraToks = new List<string>();
                if ((extra & 1) != 0) extraToks.Add("distribX");
                if ((extra >> 1 & 1) != 0) extraToks.Add("distribY");
                if ((extra >> 2 & 1) != 0) extraToks.Add("distribZ");
                if ((extra >> 3 & 1) != 0) extraToks.Add("teamFriendly");
                KV("flags", string.Format("0x{0:X8} {1}", flags, string.Join(" ", toks)));
                KV("extraFlags", string.Format("0x{0:X8} {1}", extra, string.Join(" ", extraToks)));

                // ---- scalars (T7-shifted block) ----
                if (looping)
                {
                    KV("spawnLooping", IPair(0x108));
                    KV("spawnLoopingSpawnCount", IPair(0x110));
                }
                else
                {
                    KV("spawnOneShot", IPair(0x108));
                    KV("spawnLoopingSpawnCount", IPair(0x110));
                }
                KV("spawnDelayMsec", IPair(0x120));
                KV("lifeSpanMsec", IPair(0x128));
                KV("spawnRange", FPair(0x130));
                KV("fadeInRange", FPair(0x138));
                KV("fadeOutRange", FPair(0x140));
                KV("spawnOrgX", FPair(0x148));
                KV("spawnOrgY", FPair(0x150));
                KV("spawnOrgZ", FPair(0x158));
                KV("spawnOffsetRadius", FPair(0x160));
                KV("spawnOffsetHeight", FPair(0x168));
                KV("spawnAnglePitch", DegPair(0x170));
                KV("spawnAngleYaw", DegPair(0x178));
                KV("spawnAngleRoll", DegPair(0x180));
                KV("angleVelPitch", DegMsPair(0x188));
                KV("angleVelYaw", DegMsPair(0x190));
                KV("angleVelRoll", DegMsPair(0x198));
                KV("initialRot", DegPair(0x1A0));
                KV("gravity", Pair(F0(0x1A8) * 100, F0(0x1AC) * 100));
                KV("elasticity", FPair(0x1B0));
                KV("emitDist", FPair(0x1B8));
                KV("emitDistVariance", FPair(0x1C0));
                KV("billboardPivot", Pair(F0(0x214) / 2, F0(0x218) / 2));
                KV("alphaDissolve", N(F0(0x21C)));
                KV("zFeather", N(F0(0x220)));
                KV("falloffBeginAngle", I(0x228).ToString());
                KV("falloffEndAngle", I(0x22C).ToString());
                KV("spawnFrustumCullRadius", N(F0(0x234)));
                KV("alphaFadeTimeMsec", I(0x25C).ToString());
                KV("displacement", c[0x274].ToString());
                KV("lightingFrac", N(c[0x275] / 255.0));

                // ---- atlas ----
                KV("atlas", string.Format("behavior 0x{0:X2} index {1} fps {2} loopCount {3} colBits {4} rowBits {5} indexRange {6}",
                    c[0x265], c[0x266], c[0x267], c[0x268], c[0x269], c[0x26A], c[0x26B]));

                // ---- sounds (60-bit alias hashes inline) ----
                KV("spawnSound", HashRef(BitConverter.ToUInt64(c, 0xB0), "sound"));
                KV("followSound", HashRef(BitConverter.ToUInt64(c, 0xB8), "sound"));

                // ---- fx refs ----
                KV("fxOnImpact", AssetRef(c, 0xC8, instance));
                KV("fxOnDeath", AssetRef(c, 0xD8, instance));
                KV("emission", AssetRef(c, 0xE8, instance));
                KV("attachment", AssetRef(c, 0xF8, instance));

                // ---- visuals ----
                sb.Append("\tvisuals\n\t{\n");
                if (elemType == 11)
                {
                    // lensFlare: the klf def's GUID sits inline at +0x00
                    var guid = new byte[16];
                    Buffer.BlockCopy(c, 0x00, guid, 0, 16);
                    sb.Append("\t\t\"").Append(new Guid(guid).ToString()).Append("\"\n");
                }
                else
                    foreach (var v in ReadVisuals(c, visualCount, elemType, instance))
                        sb.Append("\t\t\"").Append(v).Append("\"\n");
                sb.Append("\t};\n");
                var compute = ReadVisuals(c, visualCount, elemType, instance, 0x40);
                if (compute.Count > 0 && compute[0] != "")
                {
                    sb.Append("\tcomputeVisuals\n\t{\n");
                    foreach (var v in compute)
                        sb.Append("\t\t\"").Append(v).Append("\"\n");
                    sb.Append("\t};\n");
                }

                KV("counts", string.Format("visualCount {0} velN {1} velN2 {2} visN {3}", visualCount, velN, c[0x272], visN));

                // ---- graphs ----
                WriteVelGraphs(sb, c, velN, instance);
                WriteVisGraphs(sb, c, visN, elemType, instance);

                // ---- unmapped nonzero ranges (nothing gets lost) ----
                HexIfNonZero(sb, "\t", c, 0x08, 0x38, "unk008");
                HexIfNonZero(sb, "\t", c, 0x48, 0x38, "unk048");
                HexIfNonZero(sb, "\t", c, 0xC0, 0x08, "unk0C0");
                HexIfNonZero(sb, "\t", c, 0x100, 0x08, "unk100");
                HexIfNonZero(sb, "\t", c, 0x1C8, 0x4C, "unk1C8");
                HexIfNonZero(sb, "\t", c, 0x224, 0x04, "unk224");
                HexIfNonZero(sb, "\t", c, 0x230, 0x04, "unk230");
                HexIfNonZero(sb, "\t", c, 0x238, 0x24, "unk238");
                HexIfNonZero(sb, "\t", c, 0x261, 0x03, "unk261");
                HexIfNonZero(sb, "\t", c, 0x26C, 0x02, "unk26C");
                HexIfNonZero(sb, "\t", c, 0x26F, 0x02, "unk26F");
                HexIfNonZero(sb, "\t", c, 0x270, 0x01, "unk270");
                HexIfNonZero(sb, "\t", c, 0x276, 0x0A, "unk276");

                sb.Append("}\n");
            }

            private void WriteVelGraphs(StringBuilder sb, byte[] c, int velN, HydraInstance instance)
            {
                long velPtr = BitConverter.ToInt64(c, 0x88);
                if (!IsCanonicalPointer(velPtr))
                {
                    for (int half = 0; half < 2; half++)
                        for (int ax = 0; ax < 3; ax++)
                            Graph.Flat(0, 0).Write(sb, "velGraph" + half + "XYZ"[ax]);
                    return;
                }

                var vel = ReadChunked(instance, velPtr, (velN + 1) * VelRec);
                var times = SampleTimes(velN);
                // T7-style encoding: sample = A*scale/(1000*velN)
                double vmul = 1000.0 * Math.Max(velN, 1);
                for (int half = 0; half < 2; half++)
                {
                    for (int ax = 0; ax < 3; ax++)
                    {
                        var a = new double[velN + 1][];
                        var b = new double[velN + 1][];
                        for (int s = 0; s <= velN; s++)
                        {
                            int o = s * VelRec + half * 0x30;
                            double bas = BitConverter.ToSingle(vel, o + ax * 4) * vmul;
                            double amp = BitConverter.ToSingle(vel, o + 12 + ax * 4) * vmul;
                            a[s] = new[] { bas };
                            b[s] = new[] { bas + amp };
                        }
                        Graph.Factor(times, a, b).Write(sb, "velGraph" + half + "XYZ"[ax]);
                    }
                }
            }

            private void WriteVisGraphs(StringBuilder sb, byte[] c, int visN, int elemType, HydraInstance instance)
            {
                long visPtr = BitConverter.ToInt64(c, 0x98);
                if (!IsCanonicalPointer(visPtr))
                    return;

                var vis = ReadChunked(instance, visPtr, (visN + 1) * VisRec);
                var times = SampleTimes(visN);
                double rmul = 1000.0 * Math.Max(visN, 1) * Rad2Deg;

                double[][] Rgb(int off)
                {
                    var vals = new double[visN + 1][];
                    for (int s = 0; s <= visN; s++)
                    {
                        int o = s * VisRec + off;
                        vals[s] = new[] { vis[o] / 255.0, vis[o + 1] / 255.0, vis[o + 2] / 255.0 };
                    }
                    return vals;
                }
                double[][] Bytes1(int off)
                {
                    var vals = new double[visN + 1][];
                    for (int s = 0; s <= visN; s++)
                        vals[s] = new[] { vis[s * VisRec + off] / 255.0 };
                    return vals;
                }
                double[][] Fl(int off, double mul, bool amp = false)
                {
                    var vals = new double[visN + 1][];
                    for (int s = 0; s <= visN; s++)
                    {
                        double v = BitConverter.ToSingle(vis, s * VisRec + off) * mul;
                        if (amp)
                            v += BitConverter.ToSingle(vis, s * VisRec + off - VisHalf) * mul;
                        vals[s] = new[] { v };
                    }
                    return vals;
                }

                Graph.Factor(times, Rgb(0x00), Rgb(VisHalf + 0x00)).Write(sb, "colorGraph");
                Graph.Factor(times, Bytes1(0x03), Bytes1(VisHalf + 0x03)).Write(sb, "alphaGraph");
                Graph.Factor(times, Fl(0x04, rmul), Fl(VisHalf + 0x04, rmul, true)).Write(sb, "rotGraph");
                Graph.Factor(times, Fl(0x0C, 2), Fl(VisHalf + 0x0C, 2, true)).Write(sb, "sizeGraph0");
                Graph.Factor(times, Fl(0x10, 2), Fl(VisHalf + 0x10, 2, true)).Write(sb, "sizeGraph1");
                Graph.Factor(times, Fl(0x14, 1), Fl(VisHalf + 0x14, 1, true)).Write(sb, "scaleGraph");
                if (elemType == 8)
                {
                    Graph.Factor(times, Fl(0x20, 1), Fl(VisHalf + 0x20, 1, true)).Write(sb, "lightIntensityGraph");
                    Graph.Factor(times, Fl(0x28, 1), Fl(VisHalf + 0x28, 1, true)).Write(sb, "lightRadiusGraph");
                    Graph.Factor(times, Fl(0x30, 1), Fl(VisHalf + 0x30, 1, true)).Write(sb, "lightFovGraph");
                }
            }

            private List<string> ReadVisuals(byte[] c, int visualCount, int elemType, HydraInstance instance, int slot = 0x00)
            {
                var result = new List<string>();
                long ptr = BitConverter.ToInt64(c, slot);
                if (!IsCanonicalPointer(ptr) || elemType == 8)
                {
                    result.Add("");
                    return result;
                }

                // single visual: ptr -> asset header (hash at +0);
                // multiple: ptr -> array of asset pointers
                if (visualCount <= 1)
                {
                    result.Add(ResolveAsset(ptr, instance));
                }
                else
                {
                    for (int v = 0; v < visualCount; v++)
                    {
                        var entry = instance.Reader.ReadInt64(ptr + v * 8);
                        result.Add(IsCanonicalPointer(entry) ? ResolveAsset(entry, instance) : "");
                    }
                }
                return result;
            }

            /// <summary>
            /// Resolves an asset header pointer to a name via its inline 60-bit
            /// hash (material/xmodel/fx all store it at +0)
            /// </summary>
            private string ResolveAsset(long ptr, HydraInstance instance)
            {
                var raw = (ulong)instance.Reader.ReadInt64(ptr);
                if (raw == 0)
                    return "";
                var hash = raw & HashMask;
                return HashIndex.TryGetValue(hash, out var name) ? name : string.Format("hash_{0:x}", hash);
            }

            private string AssetRef(byte[] c, int off, HydraInstance instance)
            {
                long ptr = BitConverter.ToInt64(c, off);
                if (!IsCanonicalPointer(ptr))
                    return "\"\"";
                return "\"" + ResolveAsset(ptr, instance) + "\"";
            }

            private static string HashRef(ulong raw, string prefix)
            {
                if (raw == 0)
                    return "\"\"";
                return "\"" + GetHashName(raw & HashMask, prefix) + "\"";
            }

            private static double[] SampleTimes(int n)
            {
                var times = new double[n + 1];
                for (int s = 0; s <= n; s++)
                    times[s] = n > 0 ? (double)s / n : 0;
                return times;
            }

            private static double F(byte[] b, int off) => BitConverter.ToSingle(b, off);

            private static void HexIfNonZero(StringBuilder sb, string indent, byte[] data, int off, int len, string label)
            {
                if (off + len > data.Length)
                    len = data.Length - off;
                if (len <= 0)
                    return;
                bool any = false;
                for (int i = 0; i < len; i++)
                    if (data[off + i] != 0) { any = true; break; }
                if (!any)
                    return;
                sb.Append(indent).Append(label).Append(' ');
                for (int i = 0; i < len; i++)
                    sb.Append(data[off + i].ToString("X2"));
                sb.Append(";\n");
            }
            #endregion

            #region RawDumps
            private string AnnotateHeader(long address, byte[] header)
            {
                var sb = new StringBuilder();
                sb.AppendFormat("address 0x{0:X}\nsize 0x{1:X}\n\n", address, header.Length);
                for (int off = 0; off + 8 <= header.Length; off += 8)
                {
                    var u64 = BitConverter.ToUInt64(header, off);
                    sb.AppendFormat("+0x{0:X3}  {1:X16}  i32: {2,11} {3,11}  f32: {4,14:G6} {5,14:G6}",
                        off, u64,
                        BitConverter.ToInt32(header, off), BitConverter.ToInt32(header, off + 4),
                        BitConverter.ToSingle(header, off), BitConverter.ToSingle(header, off + 4));
                    if (IsCanonicalPointer((long)u64))
                        sb.Append("  PTR");
                    else if ((u64 & HashMask) == u64 && u64 > 0xFFFFFFFF)
                        sb.Append("  HASH?");
                    sb.Append('\n');
                }
                return sb.ToString();
            }

            private void DumpPointerTargets(HydraInstance instance, string baseName, byte[] header, byte[] elems)
            {
                var seen = new HashSet<long>();
                var index = new StringBuilder();

                using (var stream = File.Create(baseName + ".ptrs.bin"))
                {
                    void Chase(string label, byte[] buffer, int start, int length)
                    {
                        for (int off = start; off + 8 <= start + length; off += 8)
                        {
                            var target = BitConverter.ToInt64(buffer, off);
                            if (!IsCanonicalPointer(target) || !seen.Add(target))
                                continue;

                            var data = ReadChunked(instance, target, PointerChaseSize);
                            index.AppendFormat("{0}+0x{1:X3} -> 0x{2:X} @ file 0x{3:X} len 0x{4:X}\n",
                                label, off - start, target, stream.Position, data.Length);
                            stream.Write(data, 0, data.Length);
                        }
                    }

                    Chase("hdr", header, 0, header.Length);
                    if (elems != null)
                        for (int e = 0; e * ElemSize < elems.Length; e++)
                            Chase("elem" + e, elems, e * ElemSize, Math.Min(ElemSize, elems.Length - e * ElemSize));
                }

                File.WriteAllText(baseName + ".ptrs.txt", index.ToString());
            }
            #endregion

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

            public bool IsNullAsset(Asset asset)
            {
                return IsNullAsset((long)asset.Data);
            }

            public bool IsNullAsset(long nameAddress)
            {
                return nameAddress >= StartAddress && nameAddress <= EndAddress || nameAddress == 0;
            }
        }
    }
}
