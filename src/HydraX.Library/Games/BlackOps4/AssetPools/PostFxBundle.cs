using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HydraX.Library
{
    public partial class BlackOps4
    {
        /// <summary>
        /// Black Ops 4 PostFX Bundle Logic
        ///
        /// T8 promoted postfxbundle from a T7 script bundle to a real asset
        /// (pool 102, header 0x38). The linker BAKES the GDT stages/constants
        /// into per-scalar-channel keyframe tracks:
        ///
        ///   header 0x38: +0x00 name hash, +0x14 enterStage(u8), +0x15 exitStage,
        ///     +0x16 finishLoopOnExit, +0x17 firstPersonOnly, +0x1A looping,
        ///     +0x1B screenCapture(best guess), +0x1E numStages, +0x1F numTracks,
        ///     +0x20 PostfxStage*, +0x28 track descriptor array*,
        ///     +0x30 unknown ptr (8/43 assets). +0x18/19/1C/1D unknown smalls.
        ///   stage 0x30 (dev size kept): +0x00 length f32, +0x08 Material*,
        ///     +0x25 numBindings(u8), +0x28 bindings*. (+0x23/+0x26 rare unknowns;
        ///     dev method/cull/spriteFilter/thermal fields read 0 everywhere.)
        ///   binding 0x20: +0x08 curve content-hash (dedupe key, shared across
        ///     bundles with identical curves), +0x18 flag, +0x19 scriptVector
        ///     index (0-7; 0x0A = global/system track), +0x1A channel bit
        ///     (1/2/4/8 = x/y/z/w), +0x1B track index. Identical per stage.
        ///   descriptor 0x10: +0x00 type (1=curve, 2=empty), +0x01 numKeys,
        ///     +0x08 keys* (null when numKeys 0).
        ///   key 0x18: f32 time (bundle-cumulative), f32 value, 3x f32 zero,
        ///     u8 anm (PostfxAnimation: 1 linear 2 step 3-5 ease in/out/inout
        ///     6 repeat 7 mirror 8 sin), 3 bytes linker garbage. Holds are flat
        ///     segments; steps are duplicate-time key pairs.
        ///
        /// Export un-bakes the tracks per stage window back into BO3
        /// postfxbundle.gdf constants (delay/start/end per channel + anm) and
        /// writes the same shipping-format GDT as the BO3 pool, plus a
        /// _tracks.txt sidecar with the raw baked curves (the BO4-native
        /// ground truth the GDT reconstruction approximates).
        /// </summary>
        private class PostFxBundle : IAssetPool
        {
            /// <summary>PostfxAnimation names for the anm segment codes</summary>
            private static readonly string[] AnimNames =
            {
                "hold", "linear", "step", "ease in", "ease out", "ease inout",
                "linear repeat", "linear mirror", "sin"
            };

            private struct Key
            {
                public float Time;
                public float Value;
                public byte Anim;
            }

            private class Track
            {
                public int ScriptVector;
                public int Channel;      // 0-3 = x y z w
                public byte Flag;
                public ulong CurveHash;
                public Key[] Keys;
                public float Span;   // max |value| over the whole track — the
                                     // error normalizer, global so that fit
                                     // scores compare across window sizes
            }

            public int AssetSize { get; set; }
            public int AssetCount { get; set; }
            public long StartAddress { get; set; }
            public long EndAddress { get { return StartAddress + (AssetCount * AssetSize); } set => throw new NotImplementedException(); }
            public string Name => "postfxbundle";
            public string SettingGroup => "Misc";
            public int Index => 102;

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

                if (!IsCanonicalPointer(StartAddress) || AssetSize != 0x38 || AssetCount <= 0 || AssetCount > 0x100000)
                    return results;

                for (int i = 0; i < AssetCount; i++)
                {
                    var address = StartAddress + (i * AssetSize);
                    var rawHash = (ulong)instance.Reader.ReadInt64(address);

                    // free slots are 0 or a freelist pointer into the pool; live
                    // hashes can have a ZERO top nibble here (pstfx_infrared,
                    // pstfx_water_t_out, pstfx_zm_acid_dmg) so no >>60 test
                    if (rawHash == 0 || IsNullAsset((long)rawHash))
                        continue;

                    var header = instance.Reader.ReadBytes(address, AssetSize);
                    var stagesPtr = BitConverter.ToInt64(header, 0x20);

                    results.Add(new Asset()
                    {
                        Name        = GetHashName(rawHash, "pstfx"),
                        Type        = Name,
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                        Zone        = ((BlackOps4)instance.Game).GetZoneName(stagesPtr),
                        Information = string.Format("{0} Stage{1}{2}, {3} Track{4}",
                            header[0x1E], header[0x1E] == 1 ? "" : "s",
                            header[0x1A] != 0 ? ", Looping" : "",
                            header[0x1F], header[0x1F] == 1 ? "" : "s")
                    });
                }

                return results;
            }

            /// <summary>
            /// Exports the given bundle as a BO3 postfxbundle.gdf GDT + raw track sidecar
            /// </summary>
            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var d = instance.Reader.ReadBytes((long)asset.Data, AssetSize);
                if (d == null || d.Length < AssetSize)
                    throw new Exception("Failed to read postfxbundle header");

                int numStages = d[0x1E];
                int numTracks = d[0x1F];
                var stagesPtr = BitConverter.ToInt64(d, 0x20);
                var tracksPtr = BitConverter.ToInt64(d, 0x28);

                var notes = new List<string>();
                if (d[0x18] != 0) notes.Add(string.Format("hdr+0x18 = {0} (unknown)", d[0x18]));
                if (d[0x19] != 0) notes.Add(string.Format("hdr+0x19 = {0} (unknown)", d[0x19]));
                if (d[0x1C] != 0) notes.Add(string.Format("hdr+0x1C = {0} (unknown)", d[0x1C]));
                if (d[0x1D] != 2) notes.Add(string.Format("hdr+0x1D = {0} (unknown, usually 2)", d[0x1D]));
                if (BitConverter.ToInt64(d, 0x30) != 0) notes.Add(string.Format("hdr+0x30 = 0x{0:X} (unknown pointer)", BitConverter.ToInt64(d, 0x30)));

                // ---- tracks (descriptors + keys) ----
                var tracks = new Track[numTracks];
                for (int t = 0; t < numTracks; t++)
                {
                    var rec = instance.Reader.ReadBytes(tracksPtr + t * 0x10, 0x10);
                    int numKeys = rec[1];
                    var keysPtr = BitConverter.ToInt64(rec, 8);
                    var keys = new Key[numKeys];
                    if (numKeys > 0 && IsCanonicalPointer(keysPtr))
                    {
                        var kd = instance.Reader.ReadBytes(keysPtr, numKeys * 0x18);
                        for (int k = 0; k < numKeys; k++)
                            keys[k] = new Key
                            {
                                Time  = BitConverter.ToSingle(kd, k * 0x18),
                                Value = BitConverter.ToSingle(kd, k * 0x18 + 4),
                                Anim  = kd[k * 0x18 + 0x14],
                            };
                    }
                    tracks[t] = new Track
                    {
                        Keys = keys,
                        ScriptVector = -1,
                        Span = keys.Length > 0 ? keys.Max(x => Math.Abs(x.Value)) : 0,
                    };
                }

                // ---- stages + bindings (binding lists repeat per stage; keep stage 0's) ----
                var stageLengths = new float[numStages];
                var stageMaterials = new string[numStages];
                for (int s = 0; s < numStages; s++)
                {
                    var sd = instance.Reader.ReadBytes(stagesPtr + s * 0x30, 0x30);
                    stageLengths[s] = BitConverter.ToSingle(sd, 0);
                    stageMaterials[s] = MaterialName(instance, BitConverter.ToInt64(sd, 8));
                    if (sd[0x23] != 0) notes.Add(string.Format("stage{0}+0x23 = {1} (unknown)", s, sd[0x23]));
                    if (sd[0x26] != 0) notes.Add(string.Format("stage{0}+0x26 = {1} (unknown)", s, sd[0x26]));

                    if (s != 0)
                        continue;
                    int numBindings = sd[0x25];
                    var bindPtr = BitConverter.ToInt64(sd, 0x28);
                    for (int b = 0; b < numBindings; b++)
                    {
                        var bd = instance.Reader.ReadBytes(bindPtr + b * 0x20, 0x20);
                        int trackIdx = bd[0x1B];
                        if (trackIdx >= numTracks)
                            continue;
                        var bit = bd[0x1A];
                        tracks[trackIdx].ScriptVector = bd[0x19];
                        tracks[trackIdx].Channel = bit == 8 ? 3 : bit == 4 ? 2 : bit == 2 ? 1 : 0;
                        tracks[trackIdx].Flag = bd[0x18];
                        tracks[trackIdx].CurveHash = (ulong)BitConverter.ToInt64(bd, 8);
                    }
                }

                // ---- emit GDT ----
                var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var assets = new SortedSet<string>(StringComparer.Ordinal);

                values["configstringFileType"] = "SCRIPTBUNDLE";
                values["vmType"] = "Client";
                values["type"] = "postfxbundle";
                values["num_stages"] = numStages.ToString();
                if (d[0x14] != 0) values["enterStage"] = "1";
                if (d[0x15] != 0) values["exitStage"] = "1";
                if (d[0x16] != 0) values["finishLoopOnExit"] = "1";
                if (d[0x17] != 0) values["firstpersononly"] = "1";
                if (d[0x1A] != 0) values["looping"] = "1";
                if (d[0x1B] != 0) values["screenCapture"] = "1";

                float t0 = 0;
                // stage windows: BO4 curves are often richer than one BO3
                // constant per stage can express (ramp-then-hold, asymmetric
                // pulses...). BO3 allows 10 stages and BO4 bundles use 1-3, so
                // non-looping bundles get their stages greedily SPLIT at track
                // key times until everything fits or the budget runs out.
                // Looping bundles keep their enter/loop/exit structure.
                var svGroups = tracks.Where(x => x.ScriptVector >= 0 && x.ScriptVector <= 7)
                                     .GroupBy(x => x.ScriptVector).OrderBy(g => g.Key)
                                     .Select(g => g.ToList()).ToList();

                var windows = new List<Tuple<float, float, int>>();
                int originalStages = numStages;
                bool looping = d[0x1A] != 0;
                bool hasEnter = d[0x14] != 0, hasExit = d[0x15] != 0;
                if (numStages == 0)
                {
                    // nothing to window (postfx_bundle_default)
                }
                else if (!looping)
                {
                    // a one-shot BO3 stage only carries material + length, so
                    // BO4's boundaries need not survive: merge same-material
                    // runs and let the splitter re-partition around the curves
                    // (a BO4 boundary mid-curve would force truncated eases)
                    float runStart = 0;
                    int runStage = 0;
                    float t = 0;
                    for (int s = 0; s < numStages; s++)
                    {
                        if (s > 0 && stageMaterials[s] != stageMaterials[runStage])
                        {
                            windows.Add(Tuple.Create(runStart, t, runStage));
                            runStart = t;
                            runStage = s;
                        }
                        t += stageLengths[s];
                    }
                    if (t > runStart || windows.Count == 0)
                        windows.Add(Tuple.Create(runStart, t, runStage));
                    GreedySplit(windows, svGroups, 10);
                }
                else
                {
                    for (int s = 0; s < numStages; s++)
                    {
                        windows.Add(Tuple.Create(t0, t0 + stageLengths[s], s));
                        t0 += stageLengths[s];
                    }
                    // looping caps at enter+loop+exit: an absent enter/exit
                    // stage is spare headroom for one split at each end
                    if (!hasEnter && windows.Count < 3 && SplitOnce(windows, svGroups, 0))
                        hasEnter = true;
                    if (!hasExit && windows.Count < 3 && SplitOnce(windows, svGroups, windows.Count - 1))
                        hasExit = true;
                }
                if (windows.Count > originalStages)
                    notes.Add(string.Format("split {0} BO4 stage(s) into {1} BO3 stages at track key times to fit the curves",
                                            originalStages, windows.Count));

                values["num_stages"] = windows.Count.ToString();
                if (hasEnter) values["enterStage"] = "1"; else values.Remove("enterStage");
                if (hasExit) values["exitStage"] = "1"; else values.Remove("exitStage");

                for (int w = 0; w < windows.Count; w++)
                {
                    var prefix = string.Format("s{0:00}_", w);
                    float w0 = windows[w].Item1, w1 = windows[w].Item2;
                    var material = stageMaterials[windows[w].Item3];

                    values[prefix + "length"] = N(w1 - w0);
                    if (material != null)
                    {
                        values[prefix + "material"] = material;
                        assets.Add("material " + material);
                    }

                    int c = 0;
                    foreach (var group in svGroups)
                    {
                        var cp = string.Format("{0}c{1:00}_", prefix, c);

                        FitGroup(group, w0, w1, out byte bestAnim, out var bestFits, out float bestWorst);

                        if (bestWorst > 0.01f)
                            notes.Add(string.Format("stage{0} sv{1} approximated ({2:0.#}% off — curve not expressible as one constant, see tracks sidecar)",
                                                    w, group[0].ScriptVector, bestWorst * 100));

                        foreach (var pair in bestFits)
                        {
                            var suffix = "xyzw"[pair.Key.Channel].ToString();
                            var fit = pair.Value;
                            if (fit.Start != 0) values[cp + "start_" + suffix] = N(fit.Start);
                            // end==start must still be written when nonzero: the
                            // constant's single anm is shared across channels, and
                            // a missing end_ means "animate to 0", not "hold"
                            if (fit.End != 0) values[cp + "end_" + suffix] = N(fit.End);
                            if (fit.Delay > 0) values[cp + "delay_" + suffix] = N(fit.Delay);
                        }

                        values[cp + "name"] = "scriptVector" + group[0].ScriptVector;
                        values[cp + "channels"] = (group.Max(x => x.Channel) + 1).ToString();
                        values[cp + "anm"] = AnimNames[bestAnim];
                        c++;
                    }
                    if (c > 0)
                        values[prefix + "num_consts"] = c.ToString();
                    // BO4 dropped T7's 4-constants-per-stage cap; BO3 mod tools
                    // only honour c00-c03, so flag anything beyond
                    if (c > 4 && w == 0)
                        notes.Add(string.Format("uses {0} constants per stage — BO3 supports 4 (c04+ will be ignored by BO3 tools)", c));
                }

                foreach (var track in tracks.Where(x => x.ScriptVector > 7))
                    notes.Add(string.Format("system track sv=0x{0:X} ({1} keys) not emitted", track.ScriptVector, track.Keys.Length));

                var sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("\t\"").Append(asset.Name).Append("\" ( \"postfxbundle.gdf\" )\r\n");
                sb.Append("\t{\r\n");
                foreach (var pair in values)
                    sb.Append("\t\t\"").Append(pair.Key).Append("\" \"")
                      .Append(pair.Value.Replace("\\", "\\\\")).Append("\"\r\n");
                sb.Append("\t}\r\n");
                sb.Append("}\r\n");

                var dir = Path.Combine("exported_files", instance.Game.Name, "postfxbundles");
                Directory.CreateDirectory(dir);
                var baseName = asset.Name.Replace('/', '_');
                File.WriteAllText(Path.Combine(dir, baseName + ".gdt"), sb.ToString());

                // ---- raw track sidecar (the BO4-native truth) ----
                var tr = new StringBuilder();
                tr.Append("# baked postfx tracks for ").Append(asset.Name).Append("\r\n");
                tr.AppendFormat("# stages={0} looping={1} enter={2} exit={3} finishLoopOnExit={4} firstPersonOnly={5} screenCapture={6}\r\n",
                    numStages, d[0x1A], d[0x14], d[0x15], d[0x16], d[0x17], d[0x1B]);
                for (int s = 0; s < numStages; s++)
                    tr.AppendFormat("# stage{0}: length={1} material={2}\r\n", s, N(stageLengths[s]), stageMaterials[s] ?? "(unresolved)");
                foreach (var note in notes)
                    tr.Append("# NOTE: ").Append(note).Append("\r\n");
                for (int t = 0; t < numTracks; t++)
                {
                    var track = tracks[t];
                    tr.AppendFormat("track {0}: sv={1} channel={2} flag={3} curvehash={4:x}\r\n",
                        t, track.ScriptVector, track.ScriptVector >= 0 ? "xyzw"[track.Channel].ToString() : "?",
                        track.Flag, track.CurveHash);
                    foreach (var key in track.Keys)
                        tr.AppendFormat("  t={0} v={1} anm={2}\r\n", N(key.Time), N(key.Value),
                            key.Anim < AnimNames.Length ? AnimNames[key.Anim] : key.Anim.ToString());
                }
                File.WriteAllText(Path.Combine(dir, baseName + "_tracks.txt"), tr.ToString());

                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                    File.WriteAllText(Path.Combine(dir, baseName + "_assets.txt"),
                                      FXEffect.AssetList(asset.Name, assets));
            }

            /// <summary>
            /// Evaluates the piecewise-linear track at t. fromLeft picks which
            /// value wins at duplicate-time step keys: false = start-of-interval
            /// (later key), true = end-of-interval (earlier key).
            /// </summary>
            private static float Eval(Key[] keys, float t, bool fromLeft)
            {
                if (keys.Length == 0)
                    return 0;
                if (t <= keys[0].Time)
                    return keys[0].Value;
                for (int i = keys.Length - 1; i >= 0; i--)
                {
                    if (keys[i].Time < t || (!fromLeft && keys[i].Time == t))
                    {
                        if (i + 1 >= keys.Length || keys[i].Time == t)
                            return keys[i].Value;
                        var a = keys[i];
                        var b = keys[i + 1];
                        return b.Time <= a.Time ? a.Value
                             : a.Value + (b.Value - a.Value) * (t - a.Time) / (b.Time - a.Time);
                    }
                }
                return keys[0].Value;
            }

            private struct ChannelFit
            {
                public float Start;
                public float End;
                public float Delay;
                public byte Anim;     // 0 = hold
                public bool Exact;
            }

            /// <summary>
            /// k(f) per PostfxAnimation, straight from the dev-ELF evaluator
            /// (GfxCorePostfxBundleEvalStageAtTime, cod_Debug.elf 0xbf0290)
            /// </summary>
            private static float CurveK(byte anm, float f)
            {
                f = Math.Max(0, Math.Min(1, f));
                switch (anm)
                {
                    case 1:
                    case 6: return f;
                    case 2: return 1;
                    case 3: return f * f;
                    case 4: return -f * (f - 2);
                    case 5:
                        f *= 2;
                        if (f < 1) return 0.5f * f * f;
                        f -= 1;
                        return -0.5f * (f * (f - 2) - 1);
                    case 7: return f > 0.5f ? 1 - f : f;
                    case 8: return 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * f);
                    default: return 0;
                }
            }

            /// <summary>
            /// True baked-track value at t: piecewise between keys, each segment
            /// eased by its own anm byte
            /// </summary>
            private static float EvalTrue(Key[] keys, float t)
            {
                if (keys.Length == 0)
                    return 0;
                if (t <= keys[0].Time)
                    return keys[0].Value;
                for (int i = keys.Length - 1; i >= 0; i--)
                {
                    if (keys[i].Time > t)
                        continue;
                    while (i + 1 < keys.Length && keys[i + 1].Time == keys[i].Time)
                        i++;
                    if (i + 1 >= keys.Length)
                        return keys[i].Value;
                    var a = keys[i];
                    var b = keys[i + 1];
                    if (b.Time <= a.Time)
                        return a.Value;
                    float f = (t - a.Time) / (b.Time - a.Time);
                    return a.Value + CurveK(a.Anim == 0 ? (byte)1 : a.Anim, f) * (b.Value - a.Value);
                }
                return keys[0].Value;
            }

            /// <summary>
            /// Fits one scriptVector's channels inside [t0, t1] under the best
            /// single anm (the GDT shares one anm across a constant's channels).
            /// Scored by the SUM of per-channel relative errors, not the max: a
            /// channel no anm can express must not drag the fittable channels'
            /// choice into a tie decided by iteration order. Returns the sum.
            /// </summary>
            private static float FitGroup(List<Track> group, float t0, float t1, out byte bestAnim, out Dictionary<Track, ChannelFit> bestFits, out float bestWorst)
            {
                var candidateAnims = new SortedSet<byte> { 0, 1, 2, 7 };
                foreach (var track in group)
                    for (int k = 0; k + 1 < track.Keys.Length; k++)
                        if (track.Keys[k].Anim >= 3 && track.Keys[k].Anim <= 8)
                            candidateAnims.Add(track.Keys[k].Anim);

                bestAnim = 0;
                bestWorst = 0;
                bestFits = new Dictionary<Track, ChannelFit>();
                float bestErr = float.MaxValue;
                foreach (var anm in candidateAnims)
                {
                    float total = 0, worst = 0;
                    var fits = new Dictionary<Track, ChannelFit>();
                    foreach (var track in group)
                    {
                        fits[track] = FitChannel(track.Keys, t0, t1, anm, out float err, out float span);
                        // amplitude floor: sub-0.01 tracks are authoring float
                        // dust; absolute error is the honest measure there
                        float rel = err / Math.Max(track.Span, 0.01f);
                        total += rel;
                        worst = Math.Max(worst, rel);
                    }
                    if (total < bestErr)
                    {
                        bestErr = total;
                        bestWorst = worst;
                        bestAnim = anm;
                        bestFits = fits;
                    }
                }
                return bestErr;
            }

            /// <summary>
            /// Total fit error of all groups inside [t0, t1]
            /// </summary>
            private static float WindowError(List<List<Track>> groups, float t0, float t1)
            {
                float total = 0;
                foreach (var group in groups)
                    total += FitGroup(group, t0, t1, out _, out _, out _);
                return total;
            }

            /// <summary>
            /// Splits one specific window at its best key time if that reduces
            /// its fit error; returns whether a split happened
            /// </summary>
            private static bool SplitOnce(List<Tuple<float, float, int>> windows, List<List<Track>> groups, int w)
            {
                float w0 = windows[w].Item1, w1 = windows[w].Item2;
                float margin = Math.Max(1e-3f, (w1 - w0) * 0.02f);
                float baseErr = WindowError(groups, w0, w1);
                if (baseErr <= 1e-3f)
                    return false;
                float bestTime = 0, bestGain = 1e-3f;
                foreach (var group in groups)
                    foreach (var track in group)
                        foreach (var key in track.Keys)
                        {
                            if (key.Time <= w0 + margin || key.Time >= w1 - margin)
                                continue;
                            float gain = baseErr - WindowError(groups, w0, key.Time) - WindowError(groups, key.Time, w1);
                            if (gain > bestGain)
                            {
                                bestGain = gain;
                                bestTime = key.Time;
                            }
                        }
                if (bestTime == 0)
                    return false;
                var old = windows[w];
                windows[w] = Tuple.Create(old.Item1, bestTime, old.Item3);
                windows.Insert(w + 1, Tuple.Create(bestTime, old.Item2, old.Item3));
                return true;
            }

            /// <summary>
            /// Greedily splits stage windows at track key times while that
            /// reduces the total fit error, up to maxStages windows — the BO3
            /// counterpart of curves BO4 authors as free keyframes
            /// </summary>
            private static void GreedySplit(List<Tuple<float, float, int>> windows, List<List<Track>> groups, int maxStages)
            {
                while (windows.Count < maxStages)
                {
                    int bestWindow = -1;
                    float bestTime = 0, bestGain = 1e-3f;
                    for (int w = 0; w < windows.Count; w++)
                    {
                        float w0 = windows[w].Item1, w1 = windows[w].Item2;
                        float margin = Math.Max(1e-3f, (w1 - w0) * 0.02f);
                        float baseErr = WindowError(groups, w0, w1);
                        if (baseErr <= 1e-3f)
                            continue;
                        var splitTimes = new SortedSet<float>();
                        foreach (var group in groups)
                            foreach (var track in group)
                                foreach (var key in track.Keys)
                                    if (key.Time > w0 + margin && key.Time < w1 - margin)
                                        splitTimes.Add(key.Time);
                        foreach (var time in splitTimes)
                        {
                            float gain = baseErr - WindowError(groups, w0, time) - WindowError(groups, time, w1);
                            if (gain > bestGain)
                            {
                                bestGain = gain;
                                bestWindow = w;
                                bestTime = time;
                            }
                        }
                    }
                    if (bestWindow < 0)
                        return;
                    var old = windows[bestWindow];
                    windows[bestWindow] = Tuple.Create(old.Item1, bestTime, old.Item3);
                    windows.Insert(bestWindow + 1, Tuple.Create(bestTime, old.Item2, old.Item3));
                }
            }

            /// <summary>
            /// Fits the track's curve inside the stage window [t0, t1] to a GDT
            /// constant channel under a FIXED anm (the anm is shared by all of a
            /// constant's channels, so the caller scores anms across the group).
            /// Params follow the dev evaluation formula
            /// value = start + k(saturate((tLocal-delay)/(len-delay)))*(end-start);
            /// for mirror the triangle k peaks at 0.5, so end = 2*apex - start
            /// with delay aligning the apex. err/span report the sampled fit.
            /// </summary>
            private static ChannelFit FitChannel(Key[] keys, float t0, float t1, byte anm, out float err, out float span)
            {
                float len = t1 - t0;
                float startVal = Eval(keys, t0, false);
                float endVal = Eval(keys, t1, true);
                float eps = Math.Max(1e-4f, len * 1e-3f);

                float firstChange = t1, apexVal = startVal, apexTime = t0;
                for (int i = 0; i + 1 < keys.Length; i++)
                    if (keys[i + 1].Time > t0 + eps && keys[i].Time < t1 - eps && keys[i].Value != keys[i + 1].Value)
                        firstChange = Math.Min(firstChange, Math.Max(t0, keys[i].Time));
                span = 0;
                foreach (var key in keys)
                {
                    if (key.Time < t0 - eps || key.Time > t1 + eps)
                        continue;
                    span = Math.Max(span, Math.Abs(key.Value - startVal));
                    if (Math.Abs(key.Value - startVal) > Math.Abs(apexVal - startVal))
                    {
                        apexVal = key.Value;
                        apexTime = key.Time;
                    }
                }

                var fit = new ChannelFit { Start = startVal, Anim = anm };
                if (anm == 0 || span == 0)
                {
                    fit.End = startVal;
                    fit.Delay = 0;
                }
                else if (anm == 7)
                {
                    fit.End = 2 * apexVal - startVal;
                    fit.Delay = Math.Max(0, 2 * (apexTime - t0) - len);
                }
                else
                {
                    fit.End = endVal;
                    fit.Delay = Math.Max(0, firstChange - t0);
                }

                err = 0;
                float tiny = Math.Max(1e-5f, len * 1e-4f);
                for (int i = 0; i <= 64; i++)
                {
                    float t = t0 + len * i / 64f;
                    byte effective = span == 0 ? (byte)0 : anm;
                    float mine = effective == 0 || len - fit.Delay <= 0 || t - t0 <= fit.Delay
                        ? fit.Start
                        : fit.Start + CurveK(effective, (t - t0 - fit.Delay) / (len - fit.Delay)) * (fit.End - fit.Start);
                    // at a discontinuity either side's limit counts as matching,
                    // otherwise one sample landing on a step fakes a full miss
                    float sampleErr = Math.Min(Math.Abs(EvalTrue(keys, t - tiny) - mine),
                                               Math.Abs(EvalTrue(keys, t + tiny) - mine));
                    err = Math.Max(err, sampleErr);
                }
                fit.Exact = err <= Math.Max(1e-4f, span * 0.01f);
                return fit;
            }

            /// <summary>
            /// Formats a float APE-style ("1", "0.65")
            /// </summary>
            private static string N(float v)
            {
                if (Math.Abs(v - Math.Round(v)) < 1e-6 && Math.Abs(v) < 1e9)
                    return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
                return ((double)v).ToString("0.######", CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// Resolves a material asset pointer to its source name (hash at
            /// +0; compile-time category prefix stripped)
            /// </summary>
            private static string MaterialName(HydraInstance instance, long address)
            {
                if (!IsCanonicalPointer(address))
                    return null;
                var hash = (ulong)instance.Reader.ReadInt64(address) & NameBits;
                if (hash == 0)
                    return null;
                var name = GetHashName(hash, "material");
                var slash = name.IndexOf('/');
                return slash >= 0 ? name.Substring(slash + 1) : name;
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
