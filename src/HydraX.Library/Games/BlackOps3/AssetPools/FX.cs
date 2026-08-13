using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HydraX.Library
{
    partial class BlackOps3
    {
        /// <summary>
        /// Black Ops 3 FX Effect Logic — decompiles compiled FxEffectDef memory
        /// back to iwfx 3 .efx source text. Field map established empirically
        /// against the shipping share/raw/fx sources (see repo CLAUDE.md,
        /// "T7 compiled FX facts"); validated on 1,177 effects / 9,150 emitters
        /// (all scalars >= 0.999 agreement, graphs 0.97-1.00).
        /// </summary>
        private class FXEffect : IAssetPool
        {
            #region AssetStructures
            /// <summary>
            /// FX Effect Def Structure (verified: pool AssetSize == 0x90)
            /// </summary>
            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct FxEffectDefAsset
            {
                #region FxEffectDefAssetProperties
                public long NamePointer;
                public ushort Flags;
                public ushort EFPriority;
                public ushort ElemDefCountLooping;
                public ushort ElemDefCountOneShot;
                public ushort ElemDefCountEmission;
                public ushort Padding;
                public uint TotalSize;
                public uint MSECLoopingLife;
                public uint MSECNonLoopingLife;
                public long FxElementsPointer;
                public float BoundingBoxDimX;
                public float BoundingBoxDimY;
                public float BoundingBoxDimZ;
                public float BoundingCenterX;
                public float BoundingCenterY;
                public float BoundingCenterZ;
                public float OcclusionQueryDepthBias;
                public uint OcclusionQueryFadeIn;
                public uint OcclusionQueryFadeOut;
                public float OcclusionQueryScaleRangeX;
                public float OcclusionQueryScaleRangeY;
                #endregion
            }
            #endregion

            /// <summary>
            /// FxElemDef stride
            /// </summary>
            private const int ElemSize = 0x260;

            private const double Rad2Deg = 180.0 / Math.PI;

            private static readonly string[] ElemTypeNames =
            {
                "billboardSprite", "orientedSprite", "rotatedSprite", "tail",
                "line", "trail", "cloud", "model", "dynamicLight2", "light",
                "dynamicLight", "dynamicSound", "lensFlare", "decal", "runner",
                "beamSource", "beamTarget",
            };

            /// <summary>
            /// Short labels for the asset list's Info column (the full names
            /// don't fit) — index-parallel with ElemTypeNames.
            /// </summary>
            private static readonly string[] ElemTypeShort =
            {
                "Sprite", "Oriented", "Rotated", "Tail", "Line", "Trail",
                "Cloud", "Model", "DLight2", "Light", "DLight", "DSound",
                "Flare", "Decal", "Runner", "BeamSrc", "BeamTgt",
            };

            /// <summary>
            /// Builds the Info string shown in the asset list: emitter counts
            /// per section plus which element types the effect is made of.
            /// Types past the known enum (17+, none ever observed) have no
            /// decompiler support and are counted separately.
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

                var counts = new SortedDictionary<int, int>();
                int unknown = 0;
                for (int i = 0; (i + 1) * ElemSize <= elems.Length; i++)
                {
                    int t = elems[i * ElemSize + 0xC8];
                    if (t >= ElemTypeShort.Length)
                    {
                        unknown++;
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
                if (unknown > 0)
                    sb.Append(named.Count > 0 ? ", " : ": ").Append(unknown).Append(" Unknown");

                return sb.ToString();
            }

            private static readonly (int Byte, int Bit, string Token)[] FlagBits =
            {
                (0, 1, "spawnRelative"), (0, 2, "spawnFrustumCull"),
                (0, 4, "spawnOffsetSphere"), (0, 5, "spawnOffsetCylinder"),
                (1, 1, "useCollision"), (1, 2, "dieOnTouch"), (1, 3, "drawPastFog"),
                (1, 4, "drawWithViewModel"), (1, 5, "blocksSight"), (1, 6, "useItemClip"),
                (2, 0, "inheritParentMovement"), (2, 4, "alignViewpoint"),
                (2, 5, "useBillboardPivot"), (2, 6, "useGaussianCloud"),
                (2, 7, "useRotationAxis"),
                (3, 4, "nonUniformScale"), (3, 5, "noComputeSprites"),
                (3, 7, "isMatureContent"),
            };

            private static readonly (int Byte, int Bit, string Token)[] ExtraFlagBits =
            {
                (4, 0, "distribX"), (4, 1, "distribY"), (4, 2, "distribZ"),
                (4, 3, "teamFriendly"), (4, 4, "teamFoe"), (4, 5, "castShadow"),
                (4, 6, "underwaterOnly"), (4, 7, "overwaterOnly"),
                (5, 2, "gameplayIntensity"), (5, 5, "lfOccludeGeometry"),
            };

            /// <summary>
            /// A two-curve iwfx graph; keyframes are (time, values...)
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

                /// <summary>
                /// Factor absolute per-sample values into scale + normalized curves
                /// </summary>
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
                        // single-sample graphs still need two keyframes
                        if (times.Length == 1)
                        {
                            var kf = (double[])dst[0].Clone();
                            kf[0] = 1.0;
                            dst.Add(kf);
                        }
                    }
                    return g;
                }

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

            /// <summary>
            /// Formats a number like the BO3 editor (no trailing zeros)
            /// </summary>
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
                    var header = instance.Reader.ReadStruct<FxEffectDefAsset>(StartAddress + (i * AssetSize));

                    if (IsNullAsset(header.NamePointer))
                        continue;

                    var address = StartAddress + (i * AssetSize);

                    int total = header.ElemDefCountLooping + header.ElemDefCountOneShot + header.ElemDefCountEmission;

                    // one bulk read per effect: only the elemType byte at +0xC8
                    // of each record is wanted, but a read per elem costs far
                    // more in syscalls than it saves in bytes
                    byte[] elems = null;
                    if (header.FxElementsPointer != 0 && total > 0 && total < 0x800)
                        elems = instance.Reader.ReadBytes(header.FxElementsPointer, total * ElemSize);

                    results.Add(new Asset()
                    {
                        Name        = instance.Reader.ReadNullTerminatedString(header.NamePointer),
                        Type        = Name,
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                        Zone        = ((BlackOps3)instance.Game).ZoneNames.TryGetValue(address, out var zone) ? zone : "unknown",
                        Information = Describe(header.ElemDefCountLooping, header.ElemDefCountOneShot, header.ElemDefCountEmission, elems)
                    });
                }

                return results;
            }

            /// <summary>
            /// Exports the given asset from this pool as decompiled iwfx 3 text
            /// </summary>
            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var header = instance.Reader.ReadStruct<FxEffectDefAsset>((long)asset.Data);

                if (asset.Name != instance.Reader.ReadNullTerminatedString(header.NamePointer))
                    throw new Exception("The asset at the expect memory address has changed. Press the Load Game button to refresh the asset list.");

                var assets = new SortedSet<string>(StringComparer.Ordinal);
                var text = Decompile(header, instance, assets);

                var path = OutputBase(instance, asset.Name);

                // Radiant's parser requires CRLF line endings
                File.WriteAllText(path + ".efx", text.Replace("\n", "\r\n"));

                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                    File.WriteAllText(path + "_assets.txt", AssetList(asset.Name, assets));
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

            /// <summary>
            /// Decompiles a compiled FxEffectDef to iwfx 3 text
            /// </summary>
            private string Decompile(FxEffectDefAsset header, HydraInstance instance, SortedSet<string> assets)
            {
                var sb = new StringBuilder();
                sb.Append("iwfx 3\n\n");
                sb.Append("\teditorFlags;\n");
                bool hasBox = header.BoundingBoxDimX != 0 || header.BoundingBoxDimY != 0 || header.BoundingBoxDimZ != 0;
                sb.Append("\tefFlags").Append(hasBox ? " efUseBoundingBox" : "").Append(";\n");
                sb.Append("\tefPriority ").Append(header.EFPriority).Append(";\n");
                sb.AppendFormat("\tefBoundingBoxMin {0} {1} {2};\n",
                    N(header.BoundingCenterX - header.BoundingBoxDimX),
                    N(header.BoundingCenterY - header.BoundingBoxDimY),
                    N(header.BoundingCenterZ - header.BoundingBoxDimZ));
                sb.AppendFormat("\tefBoundingBoxMax {0} {1} {2};\n",
                    N(header.BoundingCenterX + header.BoundingBoxDimX),
                    N(header.BoundingCenterY + header.BoundingBoxDimY),
                    N(header.BoundingCenterZ + header.BoundingBoxDimZ));
                sb.Append("\tocclusionQueryDepthBias ").Append(N(header.OcclusionQueryDepthBias)).Append(";\n");
                sb.Append("\tocclusionQueryFadeIn ").Append(header.OcclusionQueryFadeIn).Append(";\n");
                sb.Append("\tocclusionQueryFadeOut ").Append(header.OcclusionQueryFadeOut).Append(";\n");
                sb.Append("\tocclusionQueryScaleRange ").Append(Pair(header.OcclusionQueryScaleRangeX, header.OcclusionQueryScaleRangeY)).Append(";\n");
                sb.Append("\tnormalsShape 0;\n\tnormalsShapeOffset 0 0 0;\n\tnormalsShapeRadius 0;\n");
                sb.Append("\tnormalsShapeLength 0;\n\tnormalsShapeAngles 0 0 0;\n");
                sb.Append("\tnormalsShapeVisualizationColor 0 0 0 0;\n\tefCompletion 0;\n");
                sb.Append("\tlodDefault0 0 0;\n\tlodDefault1 0 0;\n\tlodDefault2 0 0;\n\tlodDefault3 0 0;\n");

                int countL = header.ElemDefCountLooping;
                int countO = header.ElemDefCountOneShot;
                int total = countL + countO + header.ElemDefCountEmission;
                var counters = new Dictionary<string, int>();

                for (int i = 0; i < total; i++)
                {
                    var c = instance.Reader.ReadBytes(header.FxElementsPointer + i * ElemSize, ElemSize);
                    bool looping = i < countL;
                    string section = looping ? "looping" : (i < countL + countO ? "oneshot" : "emission");
                    WriteEmitter(sb, c, looping, section, counters, instance, assets);
                }

                return sb.ToString();
            }

            private void WriteEmitter(StringBuilder sb, byte[] c, bool looping, string section,
                                      Dictionary<string, int> counters, HydraInstance instance,
                                      SortedSet<string> assets)
            {
                double F(int off) => BitConverter.ToSingle(c, off);
                int I(int off) => BitConverter.ToInt32(c, off);
                string FPair(int off) => Pair(F(off), F(off + 4));
                string IPair(int off) => BitConverter.ToInt32(c, off) + " " + BitConverter.ToInt32(c, off + 4);
                string DegPair(int off) => Pair(F(off) * Rad2Deg, F(off + 4) * Rad2Deg);

                int elemType = c[0xC8];
                string typeName = elemType < ElemTypeNames.Length ? ElemTypeNames[elemType] : "unknownType" + elemType;
                counters.TryGetValue(section + typeName, out int n);
                counters[section + typeName] = n + 1;

                int velN = c[0xCA];
                int visN = c[0xCD];
                int visualCount = c[0xC9];

                // graphs from the sample arrays
                Graph[] vel0 = null, vel1 = null;
                Graph rotG = null, size0 = null, size1 = null, scaleG = null,
                      colorG = null, alphaG = null, lightI = null, lightR = null,
                      lightF = null, childScale = null;

                long velPtr = BitConverter.ToInt64(c, 0xD0);
                if (velPtr != 0)
                {
                    var vel = instance.Reader.ReadBytes(velPtr, (velN + 1) * 0x60);
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
                                int o = s * 0x60 + half * 0x30;
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

                long visPtr = BitConverter.ToInt64(c, 0xE0);
                if (visPtr != 0)
                {
                    var vis = instance.Reader.ReadBytes(visPtr, (visN + 1) * 0x50);
                    var times = SampleTimes(visN);
                    double rmul = 1000.0 * Math.Max(visN, 1) * Rad2Deg;

                    double[][] Base(Func<byte[], int, double> get, int off)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                            vals[s] = new[] { get(vis, s * 0x50 + off) };
                        return vals;
                    }
                    double[][] Amp(int baseOff, int ampOff, double mul)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                            vals[s] = new[] { (BitConverter.ToSingle(vis, s * 0x50 + baseOff) + BitConverter.ToSingle(vis, s * 0x50 + ampOff)) * mul };
                        return vals;
                    }
                    double[][] BaseF(int off, double mul)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                            vals[s] = new[] { BitConverter.ToSingle(vis, s * 0x50 + off) * mul };
                        return vals;
                    }
                    double[][] Rgb(int off)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                        {
                            int o = s * 0x50 + off;
                            vals[s] = new[] { vis[o] / 255.0, vis[o + 1] / 255.0, vis[o + 2] / 255.0 };
                        }
                        return vals;
                    }
                    double[][] AlphaBytes(int off)
                    {
                        var vals = new double[visN + 1][];
                        for (int s = 0; s <= visN; s++)
                            vals[s] = new[] { vis[s * 0x50 + off] / 255.0 };
                        return vals;
                    }

                    colorG = Graph.Factor(times, Rgb(0x00), Rgb(0x28));
                    if (colorG.Scale > 0) { NormalizeColorScale(colorG); }
                    alphaG = Graph.Factor(times, AlphaBytes(0x03), AlphaBytes(0x2B));
                    rotG = Graph.Factor(times, BaseF(0x04, rmul), Amp(0x04, 0x2C, rmul));
                    size0 = Graph.Factor(times, BaseF(0x0C, 2), Amp(0x0C, 0x34, 2));
                    size1 = Graph.Factor(times, BaseF(0x10, 2), Amp(0x10, 0x38, 2));
                    scaleG = Graph.Factor(times, BaseF(0x14, 1), Amp(0x14, 0x3C, 1));
                    lightI = Graph.Factor(times, BaseF(0x18, 1), Amp(0x18, 0x40, 1));
                    lightR = Graph.Factor(times, BaseF(0x1C, 1), Amp(0x1C, 0x44, 1));
                    lightF = Graph.Factor(times, BaseF(0x20, 1), Amp(0x20, 0x48, 1));
                    childScale = Graph.Factor(times, BaseF(0x24, 1), Amp(0x24, 0x4C, 1));
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
                childScale = childScale ?? Graph.Flat(1);

                // editor flags: looping from partition; useRand* iff B differs
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

                // flags bitfields + runRel enum
                var flags = new List<string>();
                foreach (var (bIdx, bit, tok) in FlagBits)
                    if ((c[bIdx] >> bit & 1) != 0)
                        flags.Add(tok);
                bool rr6 = (c[0] >> 6 & 1) != 0, rr7 = (c[0] >> 7 & 1) != 0, rr0 = (c[1] & 1) != 0;
                flags.Add(rr0 ? "runRelToOffsetEffectNow"
                        : rr6 && rr7 ? "runRelToOffset"
                        : rr7 ? "runRelToEffect"
                        : rr6 ? "runRelToSpawn" : "runRelToWorld");
                if (!flags.Contains("spawnOffsetSphere") && !flags.Contains("spawnOffsetCylinder"))
                    flags.Add("spawnOffsetNone");

                var extraFlags = new List<string>();
                foreach (var (bIdx, bit, tok) in ExtraFlagBits)
                    if ((c[bIdx] >> bit & 1) != 0)
                        extraFlags.Add(tok);

                // atlas
                int ab = c[0xBC];
                var atlas = new List<string> { new[] { "startFixed", "startRandom", "startIndexed", "startFixedRange" }[ab & 3] };
                if ((ab & 0x04) != 0) atlas.Add("playOverLife");
                if ((ab & 0x08) != 0) atlas.Add("loopOnlyNTimes");
                if ((ab & 0x10) != 0) atlas.Add("lerpFrames");

                // referenced assets: entries are either an asset header (name
                // ptr at +0) or a direct pointer to the name string (unlinked
                // ref — common for visuals; the address is usually unaligned).
                // Disambiguate by content: a header's first 8 bytes form a
                // canonical pointer, a string's read back as huge/garbage values.
                string AssetName(long p)
                {
                    if (p == 0) return "";
                    var np = instance.Reader.ReadInt64(p);
                    if (np > 0x10000 && np < 0x7FFFFFFFFFFF)
                    {
                        var s = instance.Reader.ReadNullTerminatedString(np);
                        if (SaneName(s)) return s.Split('|')[0];
                    }
                    var direct = instance.Reader.ReadNullTerminatedString(p);
                    return SaneName(direct) ? direct.Split('|')[0] : "";
                }
                string FxRef(int off) => AssetName(BitConverter.ToInt64(c, off));
                string StrRef(int off)
                {
                    var p = BitConverter.ToInt64(c, off);
                    if (p == 0) return "";
                    var s = instance.Reader.ReadNullTerminatedString(p);
                    return SaneName(s) ? s : "";
                }
                string fxOnImpact = FxRef(0x198);
                string fxOnDeath = FxRef(0x1A0);
                string emission = FxRef(0x1A8);
                string attachment = FxRef(0x1D0);
                string spawnSound = StrRef(0x208);
                string followSound = StrRef(0x210);

                // trail params struct at +0x1E8 — only meaningful for trail
                // elems; for other types the slot holds unrelated union data.
                // Same struct as dev-BO4 FxTrailDef bar scrollTime being f32
                // seconds: the tail is vertCount +0x14, verts ptr +0x18,
                // indCount +0x20, inds ptr +0x28 (cross-section mesh)
                double trailScroll = 0, trailRepeat = 0, trailSplit = 0, trailFadeIn = 0, trailFadeOut = 0;
                byte[] trailDef = null;
                long trailPtr = BitConverter.ToInt64(c, 0x1E8);
                if (elemType == 5 && trailPtr != 0)
                {
                    var t = instance.Reader.ReadBytes(trailPtr, 0x30) ??
                            instance.Reader.ReadBytes(trailPtr, 0x14);
                    if (t != null && t.Length >= 0x14)
                    {
                        trailScroll = BitConverter.ToSingle(t, 0x00);
                        trailRepeat = BitConverter.ToInt32(t, 0x04);
                        trailSplit = BitConverter.ToInt32(t, 0x08);
                        trailFadeIn = BitConverter.ToSingle(t, 0x0C);
                        trailFadeOut = BitConverter.ToSingle(t, 0x10);
                        trailDef = t;
                    }
                }

                // visuals
                var visuals = new List<string>();
                long visArray = BitConverter.ToInt64(c, 0xF0);
                if (elemType == 12)
                {
                    // lensFlare doesn't reference a material: the 16 bytes at
                    // +0xF0 are the flare def's GUID inline (little-endian GUID
                    // struct), matching the "uuid" key of the .klf source the
                    // klf pool exports. 41/41 verified against shipping sources.
                    var guid = new byte[16];
                    Buffer.BlockCopy(c, 0xF0, guid, 0, 16);
                    visuals.Add(new Guid(guid).ToString());
                }
                else if (elemType == 15 || elemType == 16)
                {
                    // Beams (VERIFIED live, Der Eisendrache 2026-08-07). The
                    // visuals slot holds the beam def NAME(s), not asset ptrs:
                    // visualCount <= 1 ⇒ one char[0x40] name INLINE at +0xF0
                    // (zeroed for beamTarget — the block stays empty, matching
                    // every shipping source); visualCount >= 2 ⇒ +0xF0 points
                    // at an array of visualCount inline char[0x40] names.
                    // T8 uses the same two forms at its +0x00 slot.
                    if (visualCount >= 2 && visArray != 0)
                    {
                        for (int v = 0; v < visualCount; v++)
                        {
                            var name = instance.Reader.ReadNullTerminatedString(visArray + v * 0x40);
                            if (!string.IsNullOrEmpty(name))
                                visuals.Add(name);
                        }
                    }
                    else
                    {
                        int end = 0xF0;
                        while (end < 0x130 && c[end] != 0)
                            end++;
                        if (end > 0xF0)
                            visuals.Add(Encoding.ASCII.GetString(c, 0xF0, end - 0xF0));
                    }
                }
                else if (visArray != 0 && visualCount > 0 && elemType != 8)
                {
                    for (int v = 0; v < visualCount; v++)
                    {
                        var name = AssetName(instance.Reader.ReadInt64(visArray + v * 8));
                        if (name != "")
                            visuals.Add(name);
                    }
                    // material visuals carry compile-time name decorations that
                    // source .efx never uses: a category prefix ("ei/" sprites,
                    // "el/" trails, "ec/" clouds) and auto-generated "vd/"/
                    // "vdd/" decal LOD variants duplicating the bare material.
                    // Source material names never contain '/', so strip any
                    // prefix. Runner (fx path) and model visuals are untouched.
                    if (elemType != 7 && elemType != 14)
                    {
                        var cleaned = new List<string>();
                        foreach (var v in visuals)
                        {
                            var slash = v.IndexOf('/');
                            if (slash < 0) { cleaned.Add(v); continue; }
                            var prefix = v.Substring(0, slash);
                            var bare = v.Substring(slash + 1);
                            if ((prefix == "vd" || prefix == "vdd") && (visuals.Contains(bare) || cleaned.Contains(bare)))
                                continue;
                            cleaned.Add(bare);
                        }
                        visuals = cleaned;
                    }
                }
                // beam blocks are legitimately empty (beamTarget); every other
                // type gets the "" placeholder
                if (visuals.Count == 0 && elemType != 15 && elemType != 16)
                    visuals.Add("");

                // ---- record what this emitter references ----
                string visualKind = elemType == 7 ? "xmodel" : elemType == 14 ? "fx" : elemType == 12 ? "lensflare"
                                  : (elemType == 15 || elemType == 16) ? "beam" : "material";
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

                KV("name", $"\"{section}_{typeName}_{n}\"");
                FlagLine("editorFlags", editorFlags);
                FlagLine("flags", flags);
                FlagLine("extraFlags", extraFlags);
                KV("spawnRange", FPair(0x18));
                KV("fadeInRange", FPair(0x20));
                KV("fadeOutRange", FPair(0x28));
                KV("spawnFrustumCullRadius", N(F(0x30)));
                int spawnBase = I(0x08), spawnRand = I(0x0C);
                if (looping)
                {
                    if (spawnRand == int.MaxValue) spawnRand = 0; // rand 0 compiles to INT_MAX
                    KV("spawnLooping", spawnBase + " " + spawnRand);
                    KV("spawnLoopingSpawnCount", IPair(0x10));
                    KV("spawnOneShot", spawnBase + " 0"); // editor mirrors interval
                }
                else
                {
                    KV("spawnLooping", "200 0");
                    KV("spawnLoopingSpawnCount", IPair(0x10));
                    KV("spawnOneShot", spawnBase + " " + spawnRand);
                }
                KV("spawnDelayMsec", IPair(0x34));
                KV("lifeSpanMsec", IPair(0x3C));
                KV("spawnOrgX", FPair(0x44));
                KV("spawnOrgY", FPair(0x4C));
                KV("spawnOrgZ", FPair(0x54));
                KV("spawnOffsetRadius", FPair(0x5C));
                KV("spawnOffsetHeight", FPair(0x64));
                KV("spawnOffsetCylindricalAxis", "0");
                KV("spawnAnglePitch", DegPair(0x70));
                KV("spawnAngleYaw", DegPair(0x78));
                KV("spawnAngleRoll", DegPair(0x80));
                KV("angleVelPitch", Pair(F(0x88) * Rad2Deg * 1000, F(0x8C) * Rad2Deg * 1000));
                KV("angleVelYaw", Pair(F(0x90) * Rad2Deg * 1000, F(0x94) * Rad2Deg * 1000));
                KV("angleVelRoll", Pair(F(0x98) * Rad2Deg * 1000, F(0x9C) * Rad2Deg * 1000));
                KV("initialRot", DegPair(0xA0));
                KV("rotationAxis", "0 0 0 1");
                KV("gravity", Pair(F(0xAC) * 100, F(0xB0) * 100));
                KV("elasticity", FPair(0xB4));
                KV("windinfluence", "0");
                FlagLine("atlasBehavior", atlas);
                KV("atlasIndex", c[0xBD].ToString());
                KV("atlasFps", c[0xBE].ToString());
                KV("atlasLoopCount", c[0xBF].ToString());
                KV("atlasColIndexBits", c[0xC0].ToString());
                KV("atlasRowIndexBits", c[0xC1].ToString());
                int bits = c[0xC0] + c[0xC1];
                KV("atlasEntryCount", (bits > 0 ? 1 << bits : 0).ToString()); // not stored; derived
                KV("atlasIndexRange", BitConverter.ToUInt16(c, 0xC2).ToString());
                for (int ax = 0; ax < 3; ax++) vel0[ax].Write(sb, "velGraph0" + "XYZ"[ax]);
                for (int ax = 0; ax < 3; ax++) vel1[ax].Write(sb, "velGraph1" + "XYZ"[ax]);
                rotG.Write(sb, "rotGraph");
                size0.Write(sb, "sizeGraph0");
                size1.Write(sb, "sizeGraph1");
                scaleG.Write(sb, "scaleGraph");
                childScale.Write(sb, "childSizeScaleGraph");
                colorG.Write(sb, "colorGraph");
                alphaG.Write(sb, "alphaGraph");
                lightI.Write(sb, "lightIntensityGraph");
                lightR.Write(sb, "lightRadiusGraph");
                lightF.Write(sb, "lightFovGraph");
                Graph.Flat(1).Write(sb, "inheritParentMovementGraph");
                Graph.Flat(1).Write(sb, "attractorGraph");
                KV("attractorLocalPosition", "0 0 0");
                KV("lightingFrac", N(c[0x1F1] / 255.0));
                KV("collOffset", "0 0 0");
                KV("collRadius", "0");
                KV("fxOnImpact", $"\"{fxOnImpact}\"");
                KV("fxOnDeath", $"\"{fxOnDeath}\"");
                KV("displacement", c[0x1F0].ToString());
                KV("emission", $"\"{emission}\"");
                KV("emitDist", FPair(0x1B0));
                KV("emitDistVariance", FPair(0x1B8));
                KV("emitDensity", "1 0");
                KV("emitSizeForDensity", "1");
                KV("attachment", $"\"{attachment}\"");
                KV("attachmentDensity", "1 0");
                KV("attachmentSizeForDensity", "1");
                KV("trailSplitDist", N(trailSplit));
                KV("trailScrollTime", N(trailScroll));
                KV("trailRepeatDist", N(trailRepeat));
                KV("trailFadeInDist", N(trailFadeIn));
                KV("trailFadeOutDist", N(trailFadeOut));
                KV("alphafadetimemsec", I(0x1F4).ToString());
                KV("maxwind_mag", "0");
                KV("maxwind_life", "0");
                KV("maxwind_interval", "1");
                sb.Append("\telemSpawnSound\n\t{\n");
                if (spawnSound != "") sb.Append("\t\t\"").Append(spawnSound).Append("\"\n");
                sb.Append("\t};\n");
                sb.Append("\telemFollowSound\n\t{\n");
                if (followSound != "") sb.Append("\t\t\"").Append(followSound).Append("\"\n");
                sb.Append("\t};\n");
                int cd0 = I(0x1FC), cd1 = I(0x200);
                KV("cloudDensity", (cd0 == 0 && cd1 == 0) ? "1024 0" : cd0 + " " + cd1);
                KV("spotLightFovInnerFraction", "0.5");
                KV("spotLightStartRadius", "36");
                KV("spotLightEndRadius", "196");
                KV("alphaDissolve", N(F(0x230)));
                KV("zFeather", N(F(0x234)));
                KV("falloffBeginAngle", I(0x238).ToString());
                KV("falloffEndAngle", I(0x23C).ToString());
                KV("lfSourceDir", "1 0 0");
                KV("lfSourceSize", "15");
                // optional cross-section mesh, between lfSourceSize and
                // billboardPivot like the shipping sources
                WriteTrailDef(sb, trailDef, instance);
                KV("billboardPivot", Pair(F(0x228) / 2, -F(0x22C) / 2));
                KV("levelOfDetail", "0");
                sb.Append('\t').Append(typeName).Append("\n\t{\n");
                if (elemType == 8)
                {
                    // dynamicLight2 blocks hold an embedded Radiant lightdef, not a
                    // quoted asset name. An empty "" here derails Radiant's parser
                    // for the whole file — emit the template block with the fields
                    // recovered from the +0xF0 light struct substituted in
                    // (see ReadLightDefOverrides); the light's runtime behavior also
                    // lives in the lightIntensity/Radius/Fov graphs above.
                    WriteDefaultLightDef(sb, section + n, ReadLightDefOverrides(c, instance));
                }
                else
                {
                    foreach (var v in visuals)
                        sb.Append("\t\t\"").Append(v).Append("\"\n");
                }
                sb.Append("\t};\n");
                sb.Append("}\n");
            }

            /// <summary>
            /// Writes a trail elem's optional cross-section mesh from the +0x1E8
            /// struct's tail (vertCount +0x14, verts +0x18, indCount +0x20, inds
            /// +0x28; vertex = {vec2 pos, vec2 normal, f32 texCoord}, 0x14 bytes).
            /// Source rows are "x y texCoord" — the normals are compiler-derived
            /// and dropped. Writes nothing unless both arrays read fully and sanely.
            /// </summary>
            private static void WriteTrailDef(StringBuilder sb, byte[] td, HydraInstance instance)
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
                      .Append(' ').Append(N(BitConverter.ToSingle(verts, o + 4)))
                      .Append(' ').Append(N(BitConverter.ToSingle(verts, o + 16))).Append('\n');
                }
                sb.Append("\t} {\n");
                for (int v = 0; v < indCount; v++)
                    sb.Append("\t\t").Append(BitConverter.ToUInt16(inds, v * 2)).Append('\n');
                sb.Append("\t};\n");
            }

            // Lightdef template for dynamicLight2 emitters; `overrides` replaces
            // values by key (the fields ReadLightDefOverrides recovers from the
            // compiled light struct — the rest stay at these defaults). Key set
            // mirrors shipping sources, including the duplicated ortho_effect
            // line. The name is a deterministic Radiant-style "new<number>"
            // derived from the seed so repeated exports produce identical files.
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
            /// Recovers the authored Radiant lightdef fields from a T7 dynamicLight2
            /// elem's +0xF0 light-struct pointer. Probed live on zm_factory against
            /// the shipping embedded lightdefs (36 blocks; diversity-confirmed:
            /// type i32 +0x48 (4=OMNI, 2=SPOT), falloffdistance +0x5C (10 distinct
            /// values), cut_on +0x70 / radius +0x74 (the dev cut_on/radius adjacency),
            /// far_edge +0x7C, cos(fov_outer/2) at +0x114, ortho_effect +0x130; the
            /// color triple at +0x60 is stored piecewise-sRGB-linear x 2^stops —
            /// white anchors read exactly 2^stops, which recovers stops, and the
            /// colored anchor inverts to its source values exactly). near_edge +0x78,
            /// penumbraRadius +0x9C, roundness +0x11C and superellipse +0x120 fit
            /// everywhere but were default-constant on the probe map — kept as
            /// dev-order verbatim reads; bulbLength +0x14C (single-anchor hit, zero
            /// elsewhere). NOT stored in the struct at all (full 0x800 scanned):
            /// shadowmapScale, culling_cutoff/falloff — their template defaults
            /// remain; SPOT angles would need inverting the direction vectors.
            /// </summary>
            private static Dictionary<string, string> ReadLightDefOverrides(byte[] c, HydraInstance instance)
            {
                var d = new Dictionary<string, string>();
                long p = BitConverter.ToInt64(c, 0xF0);
                if (p <= 0x10000 || p > 0x7FFFFFFFFFFF)
                    return d;
                var b = instance.Reader.ReadBytes(p, 0x160);
                if (b == null || b.Length < 0x160)
                    return d;
                int type = BitConverter.ToInt32(b, 0x48);
                if (type == 2)
                    d["PRIMARY_TYPE"] = "SPOT";
                else if (type != 4)
                    return d;   // unexpected layout — keep the full default block
                d["falloffdistance"] = N(BitConverter.ToSingle(b, 0x5C));
                float r = BitConverter.ToSingle(b, 0x60);
                float g = BitConverter.ToSingle(b, 0x64);
                float bl = BitConverter.ToSingle(b, 0x68);
                float max = Math.Max(r, Math.Max(g, bl));
                if (!float.IsNaN(max) && !float.IsInfinity(max) && max > 0)
                {
                    double Chan(float v)
                    {
                        double l = Math.Min(Math.Max(v / max, 0f), 1f);
                        return l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                    }
                    d["_color"] = string.Format("{0} {1} {2}", N(Chan(r)), N(Chan(g)), N(Chan(bl)));
                    int stops = (int)Math.Round(Math.Log(max, 2));
                    if (stops >= 1 && stops <= 31)
                        d["stops"] = stops.ToString();
                }
                d["cut_on"] = N(BitConverter.ToSingle(b, 0x70));
                d["radius"] = N(BitConverter.ToSingle(b, 0x74));
                d["near_edge"] = N(BitConverter.ToSingle(b, 0x78));
                d["far_edge"] = N(BitConverter.ToSingle(b, 0x7C));
                d["penumbraRadius"] = N(BitConverter.ToSingle(b, 0x9C));
                float cosHalf = BitConverter.ToSingle(b, 0x114);
                if (cosHalf >= -1f && cosHalf <= 1f)
                    d["fov_outer"] = N(2 * Math.Acos(cosHalf) * 180.0 / Math.PI);
                d["roundness"] = N(BitConverter.ToSingle(b, 0x11C));
                d["bulbLength"] = N(BitConverter.ToSingle(b, 0x14C));
                d["superellipse"] = string.Format("{0} {1} {2} {3}",
                    N(BitConverter.ToSingle(b, 0x120)), N(BitConverter.ToSingle(b, 0x124)),
                    N(BitConverter.ToSingle(b, 0x128)), N(BitConverter.ToSingle(b, 0x12C)));
                d["ortho_effect"] = N(BitConverter.ToSingle(b, 0x130));
                return d;
            }

            private static double[] SampleTimes(int n)
            {
                var times = new double[n + 1];
                for (int s = 0; s <= n; s++)
                    times[s] = n > 0 ? (double)s / n : 0;
                return times;
            }

            /// <summary>
            /// Renders the per-effect assets.txt: every material, model and
            /// effect the emitters reference, one "kind,name" per line.
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
                          .Append(entry.Substring(space + 1)).Append("\r\n");
                }
                return sb.ToString();
            }

            /// <summary>
            /// Sanity check for a string read from a followed pointer
            /// </summary>
            private static bool SaneName(string s)
            {
                if (string.IsNullOrEmpty(s) || s.Length > 128)
                    return false;
                foreach (var ch in s)
                    if (ch < 0x20 || ch > 0x7E)
                        return false;
                return true;
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
