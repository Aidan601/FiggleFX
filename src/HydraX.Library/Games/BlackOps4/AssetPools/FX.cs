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
        /// Black Ops 4 FX pool (index 33) — rips compiled T8 FxEffectDefs and
        /// ports them to BO3 iwfx 3 source (.efx), plus optional raw research
        /// dumps (RawDumps=true) used while reversing the layout.
        ///
        /// T8 FxElemDef layout (0x280) established 2026-08-04 by value voting
        /// against the 322 BO3-named anchor effects — see repo CLAUDE.md
        /// "T8 FxElemDef (0x280) map".
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

            // 9/10 are dev FX_ELEM_TYPE_LEGACY_OMNI_LIGHT / _DYNAMIC_SOUND,
            // named by their T7 .efx block tokens ("light"/"dynamicSound" —
            // both empty-visual types); 12 is retail-new (post-2017), the dev
            // build cannot name it and there is nothing to port it to
            private static readonly string[] ElemTypeNames =
            {
                "billboardSprite", "orientedSprite", "rotatedSprite", "tail",
                "line", "trail", "cloud", "model", "dynamicLight2", "light",
                "dynamicSound", "lensFlare", "type12", "decal", "runner",
                "beamSource", "beamTarget",
            };

            /// <summary>
            /// Short labels for the asset list's Info column (the full names
            /// don't fit) — index-parallel with ElemTypeNames.
            /// </summary>
            private static readonly string[] ElemTypeShort =
            {
                "Sprite", "Oriented", "Rotated", "Tail", "Line", "Trail",
                "Cloud", "Model", "Light", "OmniLight", "DynSound", "Flare",
                "Type12", "Decal", "Runner", "BeamSrc", "BeamTgt",
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

                    var hash = (ulong)first & NameBits;

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

                var assets = new SortedSet<string>(StringComparer.Ordinal);
                var efx = DecompileEfx(header, elems, instance, assets);

                var path = OutputBase(instance, name);

                Write(path + ".efx", efx);


                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                    File.WriteAllText(path + "_assets.txt", AssetList(name, assets));

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

            /// <summary>
            /// Builds the extension-less output path for an effect and creates
            /// its folder. With ExportFxPaths on (the default) the asset name's
            /// path under share/raw/fx is kept, which is where the effect has
            /// to be installed anyway and is the only thing keeping effects
            /// whose leaf names collide from overwriting each other; off, every
            /// effect lands flat in the fx folder under its leaf name.
            /// </summary>
            private static string OutputBase(HydraInstance instance, string name)
            {
                var relative = instance.Settings["ExportFxPaths", "Yes"] == "Yes"
                    ? name.Replace('/', Path.DirectorySeparatorChar)
                    : name.Substring(name.LastIndexOf('/') + 1);

                var path = Path.Combine("exported_files", instance.Game.Name, "fx", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                return path;
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
                      colorG = null, alphaG = null, lightI = null, lightR = null, lightF = null,
                      inhG = null, cssG = null, attG = null;

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
                        lightF = Graph.Factor(times, BaseF(0x2C, 1), Amp(0x2C, 1));   // fov = half+0x2C (154:3 anchors)
                    }
                    cssG = Graph.Factor(times, BaseF(0x30, 1), Amp(0x30, 1));          // childSizeScale = half+0x30 (19/20)
                }

                // inherit samples (+0x90): (inhN+1) FxFloatRange records, base + delta
                long inhPtr = BitConverter.ToInt64(c, 0x90);
                int inhN = c[0x270];
                if (IsCanonicalPointer(inhPtr))
                {
                    var inh = ReadChunked(instance, inhPtr, (inhN + 1) * 8);
                    var t = SampleTimes(inhN);
                    var ia = new double[inhN + 1][];
                    var ib = new double[inhN + 1][];
                    for (int s = 0; s <= inhN; s++)
                    {
                        double bas = BitConverter.ToSingle(inh, s * 8);
                        double amp = BitConverter.ToSingle(inh, s * 8 + 4);
                        ia[s] = new[] { bas };
                        ib[s] = new[] { bas + amp };
                    }
                    inhG = Graph.Factor(t, ia, ib);
                }

                // attractor samples (+0xA0): (attN+1) FxFloatRange records like
                // inherit, attN = u8 +0x271 (53/53 flag-0x40000 elems corpus-wide)
                long attPtr = BitConverter.ToInt64(c, 0xA0);
                int attN = c[0x271];
                if (IsCanonicalPointer(attPtr))
                {
                    var att = ReadChunked(instance, attPtr, (attN + 1) * 8);
                    var t = SampleTimes(attN);
                    var attA = new double[attN + 1][];
                    var attB = new double[attN + 1][];
                    for (int s = 0; s <= attN; s++)
                    {
                        double bas = BitConverter.ToSingle(att, s * 8);
                        double amp = BitConverter.ToSingle(att, s * 8 + 4);
                        attA[s] = new[] { bas };
                        attB[s] = new[] { bas + amp };
                    }
                    attG = Graph.Factor(t, attA, attB);
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
                inhG = inhG ?? Graph.Flat(1);
                cssG = cssG ?? Graph.Flat(1);
                attG = attG ?? Graph.Flat(1);

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
                // enable token mirrors BO3 artist convention: set when the graph is custom
                bool inhCustom = inhG.Scale != 1 || inhG.Differs()
                    || inhG.CurveA.Exists(kf => kf[1] != 1.0);
                if (inhCustom) editorFlags.Add("inheritParentMovementGraphEnable");
                if (attG.Differs()) editorFlags.Add("useRandomAttractorGraph");

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
                if ((flagBits >> 16 & 1) != 0) flags.Add("inheritParentMovement");   // dev FX_ELEM_INHERIT_PARENT_MOVEMENT, 99% vs anchors
                if ((flagBits >> 18 & 1) != 0) flags.Add("attractorGraphEnable");    // dev FX_ELEM_USE_ATTRACTOR_GRAPH

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
                    return IsCanonicalPointer(ptr) ? ResolveAsset(ptr, instance, "fx") : "";
                }
                string SoundName(int off)
                {
                    // an ALIAS reference, not a sound file: `soundalias_` is a
                    // separate namespace from the `sound_` of a file path, and
                    // neither extractor rips aliases, so the prefix is fixed
                    // while the hash keeps the Saluki full-63-bit format
                    ulong raw = BitConverter.ToUInt64(c, off);
                    return raw == 0 ? "" : GetHashName(raw, "soundalias");
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
                List<byte[]> lfRecords = null;
                if (elemType == 11)
                {
                    // lensFlare doesn't reference a material: FxLensFlareVisualDef
                    // record(s) whose GUID is the "uuid" key of the .klf source
                    // (asset name = fnv1a60(uuid)); see ReadLensFlareRecords for
                    // the inline-vs-array form
                    lfRecords = ReadLensFlareRecords(c, visualCount, instance);
                    foreach (var rec in lfRecords)
                    {
                        var guid = new byte[16];
                        Buffer.BlockCopy(rec, 0, guid, 0, 16);
                        visuals.Add(new Guid(guid).ToString());
                    }
                }
                else if (elemType == 15)
                {
                    // beamSource: def name(s) at +0x00
                    visuals.AddRange(BeamNames(c, visualCount, instance));
                }
                // elemType 16 (beamTarget) stays EMPTY: no shipping T7 source
                // ever names a def there (the source's def drives rendering;
                // targets are anchors) and Radiant is only validated with the
                // empty block — T8's target-side def names are dropped
                else if (elemType != 8 && elemType != 9 && elemType != 10)
                {
                    // 9 (light) / 10 (dynamicSound) reference no visual —
                    // their T7 blocks are empty; sound comes from
                    // elemSpawnSound/elemFollowSound
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
                // beam, light and dynamicSound blocks are legitimately empty;
                // every other type gets the "" placeholder Radiant expects
                if (visuals.Count == 0 && elemType != 15 && elemType != 16 &&
                    elemType != 9 && elemType != 10)
                    visuals.Add("");

                // ---- record what this emitter references ----
                string visualKind = elemType == 7 ? "xmodel" : elemType == 14 ? "fx" : elemType == 11 ? "lensflare"
                                  : (elemType == 15 || elemType == 16) ? "beam" : "material";
                for (int vi = 0; vi < visuals.Count; vi++)
                {
                    var v = visuals[vi];
                    if (v == "")
                        continue;
                    if (visualKind == "material" && IsHashPlaceholder(v))
                    {
                        // Radiant only renders fx materials whose name starts
                        // with gfx (user-verified; stock sources are
                        // 10,177/10,190 gfx_*) — the .efx spells unresolved FX
                        // materials gfx8_<placeholder> and the APE material
                        // must be created under that name. The assets row
                        // keeps the raw name with the rename noted beside it.
                        // Model-side materials are not .efx refs and are
                        // untouched.
                        assets.Add(visualKind + " " + v + " -> gfx8_" + v);
                        visuals[vi] = "gfx8_" + v;
                    }
                    else
                    {
                        assets.Add(visualKind + " " + v);
                    }
                }
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
                KV("windinfluence", N(F0(0x240)));
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
                cssG.Write(sb, "childSizeScaleGraph");
                colorG.Write(sb, "colorGraph");
                alphaG.Write(sb, "alphaGraph");
                lightI.Write(sb, "lightIntensityGraph");
                lightR.Write(sb, "lightRadiusGraph");
                lightF.Write(sb, "lightFovGraph");
                inhG.Write(sb, "inheritParentMovementGraph");
                attG.Write(sb, "attractorGraph");
                // +0x1F0 vec3 attractorLocalPosition (dev order); union space
                // for non-attractor elems, so gate on the flag
                if ((BitConverter.ToUInt32(c, 0x118) >> 18 & 1) != 0)
                    KV("attractorLocalPosition", Pair(F0(0x1F0), F0(0x1F4)) + " " + N(F0(0x1F8)));
                else
                    KV("attractorLocalPosition", "0 0 0");
                KV("lightingFrac", N(c[0x275] / 255.0));
                // collision box collMins +0x1D8 / collMaxs +0x1E4 -> offset+radius form
                double cr = 0;
                var coff = new double[3];
                for (int k = 0; k < 3; k++)
                {
                    double lo = F0(0x1D8 + k * 4), hi = F0(0x1E4 + k * 4);
                    coff[k] = (lo + hi) / 2;
                    cr += (hi - lo) / 6;
                }
                KV("collOffset", Pair(coff[0], coff[1]) + " " + N(coff[2]));
                KV("collRadius", N(cr));
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
                // trail (+0x80 extended -> FxTrailDef, dev layout verbatim):
                // scrollTimeMsec/repeatDist/splitDist i32, fadeIn/OutDist f32,
                // then the cross-section mesh counts/pointers up to +0x30
                long trailPtr = c[0x264] == 5 ? BitConverter.ToInt64(c, 0x80) : 0;
                byte[] td = trailPtr != 0
                    ? instance.Reader.ReadBytes(trailPtr, 0x30) ?? instance.Reader.ReadBytes(trailPtr, 0x14)
                    : null;
                if (td != null && td.Length >= 0x14)
                {
                    KV("trailSplitDist", BitConverter.ToInt32(td, 0x08).ToString());
                    KV("trailScrollTime", N(BitConverter.ToInt32(td, 0x00) / 1000.0));
                    KV("trailRepeatDist", BitConverter.ToInt32(td, 0x04).ToString());
                    KV("trailFadeInDist", N(BitConverter.ToSingle(td, 0x0C)));
                    KV("trailFadeOutDist", N(BitConverter.ToSingle(td, 0x10)));
                }
                else
                {
                    KV("trailSplitDist", "0");
                    KV("trailScrollTime", "0");
                    KV("trailRepeatDist", "0");
                    KV("trailFadeInDist", "0");
                    KV("trailFadeOutDist", "0");
                }
                KV("alphafadetimemsec", BitConverter.ToUInt16(c, 0x25C).ToString());
                KV("maxwind_mag", BitConverter.ToUInt16(c, 0x25E).ToString());
                KV("maxwind_life", BitConverter.ToUInt16(c, 0x262).ToString());
                KV("maxwind_interval", BitConverter.ToUInt16(c, 0x260).ToString());
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
                if (elemType == 11)
                {
                    // FxLensFlareVisualDef tail of the first record
                    var r0 = lfRecords[0];
                    KV("lfSourceDir", Pair(BitConverter.ToSingle(r0, 0x10), BitConverter.ToSingle(r0, 0x14))
                        + " " + N(BitConverter.ToSingle(r0, 0x18)));
                    KV("lfSourceSize", N(BitConverter.ToSingle(r0, 0x1C)));
                }
                else
                {
                    KV("lfSourceDir", "1 0 0");
                    KV("lfSourceSize", "15");
                }
                // optional cross-section mesh, between lfSourceSize and
                // billboardPivot like the shipping sources
                if (elemType == 5)
                    WriteTrailDef(sb, td, instance, native: false);
                KV("billboardPivot", Pair(F0(0x214) / 2, -F0(0x218) / 2));
                KV("levelOfDetail", "0");
                sb.Append('\t').Append(typeName).Append("\n\t{\n");
                if (elemType == 8)
                    WriteDefaultLightDef(sb, section + n, ReadLightDefOverrides(c, instance));
                else
                    foreach (var v in visuals)
                        sb.Append("\t\t\"").Append(v).Append("\"\n");
                sb.Append("\t};\n");
                sb.Append("}\n");
            }

            /// <summary>
            /// Writes a trail elem's cross-section mesh (FxTrailDef tail: vertCount +0x14,
            /// verts +0x18, indCount +0x20, inds +0x28; vertex = {vec2 pos, vec2 normal,
            /// f32 texCoord}, 0x14 bytes). The .efx form drops the compiler-derived
            /// normals (source rows are "x y texCoord"); the native .bo4fx form keeps all
            /// five. Writes nothing unless both arrays read fully and sanely.
            /// </summary>
            private static void WriteTrailDef(StringBuilder sb, byte[] td, HydraInstance instance, bool native)
            {
                if (td == null || td.Length < 0x30)
                    return;
                int vertCount = BitConverter.ToInt32(td, 0x14);
                long vertsPtr = BitConverter.ToInt64(td, 0x18);
                int indCount = BitConverter.ToInt32(td, 0x20);
                long indsPtr = BitConverter.ToInt64(td, 0x28);
                if (vertCount <= 0 || vertCount > 4096 || indCount <= 0 || indCount > 65536 ||
                    vertsPtr == 0 || indsPtr == 0)
                    return;
                var verts = instance.Reader.ReadBytes(vertsPtr, vertCount * 0x14);
                var inds = instance.Reader.ReadBytes(indsPtr, indCount * 2);
                if (verts == null || inds == null || verts.Length < vertCount * 0x14 || inds.Length < indCount * 2)
                    return;
                sb.Append("\ttrailDef\n\t{\n");
                for (int v = 0; v < vertCount; v++)
                {
                    int o = v * 0x14;
                    sb.Append("\t\t").Append(N(BitConverter.ToSingle(verts, o)))
                      .Append(' ').Append(N(BitConverter.ToSingle(verts, o + 4)));
                    if (native)
                        sb.Append(' ').Append(N(BitConverter.ToSingle(verts, o + 8)))
                          .Append(' ').Append(N(BitConverter.ToSingle(verts, o + 12)));
                    sb.Append(' ').Append(N(BitConverter.ToSingle(verts, o + 16))).Append('\n');
                }
                sb.Append("\t} {\n");
                for (int v = 0; v < indCount; v++)
                    sb.Append("\t\t").Append(BitConverter.ToUInt16(inds, v * 2)).Append('\n');
                sb.Append("\t};\n");
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
            /// True for an unresolved-name placeholder in either naming style
            /// ("material_&lt;16 hex&gt;" Saluki / "xmaterial_&lt;15 hex&gt;" Greyhound /
            /// "hash_&lt;hex&gt;").
            /// </summary>
            private static bool IsHashPlaceholder(string name)
            {
                int us = name.IndexOf('_');
                if (us <= 0)
                    return false;
                switch (name.Substring(0, us))
                {
                    case "material":
                    case "xmaterial":
                    case "hash":
                        break;
                    default:
                        return false;
                }
                string rest = name.Substring(us + 1);
                if (rest.Length < 12 || rest.Length > 16)
                    return false;
                foreach (char c in rest)
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                        return false;
                return true;
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
            private static void WriteDefaultLightDef(StringBuilder sb, string seed, Dictionary<string, string> overrides = null)
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
                {
                    string outLine = line;
                    if (overrides != null)
                    {
                        int sp = line.IndexOf(' ');
                        string key = sp > 0 ? line.Substring(0, sp) : line;
                        if (overrides.TryGetValue(key, out var v))
                            outLine = key + " " + v;
                    }
                    sb.Append("\t\t\t").Append(outLine).Append('\n');
                }
                sb.Append("\t\t};\n");
            }

            /// <summary>
            /// The recoverable fields of the retail light struct behind a
            /// dynamicLight2 elem's +0x00 pointer. Retail stores a BAKED
            /// GfxConfig_Light (transforms, cull products), not the authored
            /// lightdef: verified vs 32 BO3 embedded-lightdef anchors,
            /// +0xDC radius holds (30/32; the 2 misses are genuine BO4
            /// retunes, consistent with the radius*sqrt(3) cull product at
            /// +0x1D0), while the old +0xD8 cut_on (constant 0.0001 clamp),
            /// +0x138 far_edge (cookie-transform constant 0.5) and +0x350
            /// penumbraRadius (adjacent-struct bytes) were default-value
            /// artifacts and are gone. Color chroma IS recoverable: +0xB8 is
            /// the linear-HDR color triple (NaN when radius 0) — normalize
            /// and inverse-gamma to Radiant's 0-1 _color.
            /// </summary>
            private Dictionary<string, string> ReadLightDefOverrides(byte[] c, HydraInstance instance)
            {
                var d = new Dictionary<string, string>();
                long p = BitConverter.ToInt64(c, 0x00);
                if (!IsCanonicalPointer(p))
                    return d;
                var b = instance.Reader.ReadBytes(p + 0xB8, 0x28);
                if (b == null || b.Length < 0x28)
                    return d;
                d["radius"] = N(BitConverter.ToSingle(b, 0xDC - 0xB8));
                float r = BitConverter.ToSingle(b, 0x00);
                float g = BitConverter.ToSingle(b, 0x04);
                float bl = BitConverter.ToSingle(b, 0x08);
                float max = Math.Max(r, Math.Max(g, bl));
                if (!float.IsNaN(max) && !float.IsInfinity(max) && max > 0)
                {
                    // piecewise sRGB inverse — exact on the T7 anchors, so the
                    // engine's shared transfer, not a plain 2.2 gamma
                    double Chan(float v)
                    {
                        double l = Math.Min(Math.Max(v / max, 0f), 1f);
                        return l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                    }
                    d["_color"] = string.Format("{0} {1} {2}", N(Chan(r)), N(Chan(g)), N(Chan(bl)));
                }
                return d;
            }
            #endregion

            #region Element readers
            /// <summary>
            /// A lensFlare elem's FxLensFlareVisualDef record(s) — {uuid, vec3
            /// sourceDir, f32 sourceSize}, 0x20 bytes. Same two-form convention
            /// as beams/visuals: visualCount &lt;= 1 stores ONE record INLINE at
            /// +0x00, &gt;= 2 stores a pointer to an array of visualCount records
            /// (reading that pointer as an inline GUID produced garbage uuids).
            /// Null records are padding and dropped; always returns &gt;= 1 record
            /// (falling back to the inline bytes).
            /// </summary>
            private List<byte[]> ReadLensFlareRecords(byte[] c, int visualCount, HydraInstance instance)
            {
                var records = new List<byte[]>();
                if (visualCount >= 2)
                {
                    long ptr = BitConverter.ToInt64(c, 0x00);
                    if (IsCanonicalPointer(ptr))
                    {
                        var arr = ReadChunked(instance, ptr, visualCount * 0x20);
                        for (int v = 0; v < visualCount; v++)
                        {
                            var rec = new byte[0x20];
                            Buffer.BlockCopy(arr, v * 0x20, rec, 0, 0x20);
                            bool any = false;
                            for (int k = 0; k < 16; k++)
                                any |= rec[k] != 0;
                            if (any)
                                records.Add(rec);
                        }
                    }
                }
                if (records.Count == 0)
                {
                    var rec = new byte[0x20];
                    Buffer.BlockCopy(c, 0x00, rec, 0, 0x20);
                    records.Add(rec);
                }
                return records;
            }

            /// <summary>
            /// The beam def NAME(s) of a beamSource/beamTarget elem — same two
            /// forms as T7: visualCount <= 1 ⇒ one char[0x40] name INLINE at
            /// elem +0x00 (zeroed for beamTarget); visualCount >= 2 ⇒ +0x00
            /// points at an array of visualCount inline char[0x40] names.
            /// Verified corpus-wide: the inline names fnv1a60-hash to live
            /// beam-pool assets.
            /// </summary>
            private List<string> BeamNames(byte[] c, int visualCount, HydraInstance instance)
            {
                var result = new List<string>();
                long ptr = BitConverter.ToInt64(c, 0x00);
                if (visualCount >= 2 && IsCanonicalPointer(ptr))
                {
                    for (int v = 0; v < visualCount; v++)
                    {
                        var name = instance.Reader.ReadNullTerminatedString(ptr + v * 0x40);
                        if (!string.IsNullOrEmpty(name))
                            result.Add(name);
                    }
                    return result;
                }
                int end = 0;
                while (end < 0x40 && c[end] != 0)
                {
                    if (c[end] < 0x20 || c[end] > 0x7E)
                        return result;   // not a printable name
                    end++;
                }
                if (end > 0)
                    result.Add(Encoding.ASCII.GetString(c, 0, end));
                return result;
            }

            private List<string> ReadVisuals(byte[] c, int visualCount, int elemType, HydraInstance instance, int slot = 0x00)
            {
                var result = new List<string>();
                // elemType 7 (model) points at xmodels, 14 (runner) at fx defs,
                // everything else at materials — an unresolved runner ref
                // spelled material_<hex> can never match its fx_<hex>.efx
                string prefix = elemType == 7 ? "xmodel" : elemType == 14 ? "fx" : "material";
                long ptr = BitConverter.ToInt64(c, slot);
                if (!IsCanonicalPointer(ptr) || elemType == 8)
                {
                    result.Add("");
                    return result;
                }

                // single visual: ptr -> asset header (hash at +0);
                // multiple: ptr -> array of asset pointers.
                // Decals are the exception: they keep T7's mark array, i.e.
                // visualCount entries of TWO material pointers (vd/<name> then
                // its vdd/<name> twin), so the slot ALWAYS points at an array.
                int ptrCount = elemType == 13 ? visualCount * 2 : visualCount;
                if (ptrCount <= 1)
                {
                    result.Add(ResolveAsset(ptr, instance, prefix));
                }
                else
                {
                    for (int v = 0; v < ptrCount; v++)
                    {
                        var entry = instance.Reader.ReadInt64(ptr + v * 8);
                        result.Add(IsCanonicalPointer(entry) ? ResolveAsset(entry, instance, prefix) : "");
                    }
                }
                return result;
            }

            /// <summary>
            /// Resolves an asset header pointer to a name via its inline name
            /// hash (material/xmodel/fx all store it at +0). `prefix` is the
            /// asset type the slot points at, so an unresolved hash is spelled
            /// the same way the tool that exports that type spells it.
            /// </summary>
            private string ResolveAsset(long ptr, HydraInstance instance, string prefix)
            {
                var raw = (ulong)instance.Reader.ReadInt64(ptr);
                return raw == 0 ? "" : GetHashName(raw, prefix);
            }

            private static double[] SampleTimes(int n)
            {
                var times = new double[n + 1];
                for (int s = 0; s <= n; s++)
                    times[s] = n > 0 ? (double)s / n : 0;
                return times;
            }

            private static double F(byte[] b, int off) => BitConverter.ToSingle(b, off);
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
