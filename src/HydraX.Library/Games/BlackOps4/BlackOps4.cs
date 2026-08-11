using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HydraX.Library
{
    /// <summary>
    /// Black Ops 4 (T8) support — Phase D attach skeleton.
    ///
    /// Targets the SP executable ("blackops4.exe"), either retail or the
    /// Project BO4 / Shield community client (both wrap the same final build).
    /// Signature scanning is primary; hardcoded per-build offsets are the
    /// fallback (Greyhound-era build and the final build acts targets).
    /// Asset names are 60-bit FNV1a hashes stored inline — resolved via
    /// optional dictionary files, else emitted as fx_&lt;hash&gt; style names.
    /// </summary>
    public partial class BlackOps4 : IGame
    {
        #region Structures
        /// <summary>
        /// Asset Pool Data — identical layout to Black Ops 3 (0x20 stride)
        /// </summary>
        public struct AssetPoolInfo
        {
            public long PoolPointer { get; set; }
            public int AssetSize { get; set; }
            public int PoolSize { get; set; }
            public int Padding { get; set; }
            public int AssetCount { get; set; }
            public long FreeSlot { get; set; }
        }
        #endregion

        /// <summary>
        /// First xmodel pool entry's name hash — used to validate a candidate
        /// asset pools address (per Greyhound's BO4 support)
        /// </summary>
        public const ulong FirstXModelHash = 0x04647533e968c910;

        /// <summary>
        /// Dictionary key mask: the low 60 bits of an FNV1a name hash. Every
        /// hash-name dictionary in the wild (.wni, ate47, our mined.csv) is
        /// keyed on the 60-bit form, so all lookups mask to this.
        /// </summary>
        public const ulong HashMask = 0xFFFFFFFFFFFFFFF;

        /// <summary>
        /// The bits of a name field that are actually hash. The game stores the
        /// full FNV1a-64 with bit 63 cleared (verified over 9,638 fx headers:
        /// bit 63 is never set in memory, while bits 60-62 are uniformly
        /// distributed real hash bits — for a name whose true FNV1a-64 has bit
        /// 63 set, memory holds hash &amp; 0x7FFF…).
        /// </summary>
        public const ulong NameBits = 0x7FFFFFFFFFFFFFFF;

        /// <summary>
        /// How an unresolved name hash is spelled, so ripped effects reference
        /// assets by the same name the tool that extracts them writes. Only
        /// applies to the types Greyhound exports (materials, images, models,
        /// anims, sounds) — see <see cref="StyleFor"/>.
        /// </summary>
        public enum HashNameStyle
        {
            /// <summary>
            /// Saluki: "material_4674c74919cd08f7" — the stored 63-bit value,
            /// zero-padded to 16 digits, bare type prefix.
            /// </summary>
            Saluki,

            /// <summary>
            /// Greyhound: "xmaterial_674c74919cd08f7" — the 60-bit masked value,
            /// unpadded, x-prefixed type. Loses hash bits 60-62.
            /// </summary>
            Greyhound,
        }

        /// <summary>
        /// Active hash-name style (Settings.json "HashNameStyle", applied on
        /// attach and whenever the UI toggle changes)
        /// </summary>
        public static HashNameStyle NameStyle = HashNameStyle.Saluki;

        /// <summary>
        /// Parses the Settings.json value, defaulting to Saluki
        /// </summary>
        public static HashNameStyle ParseNameStyle(string value)
        {
            return string.Equals(value, "Greyhound", StringComparison.OrdinalIgnoreCase) ?
                HashNameStyle.Greyhound :
                HashNameStyle.Saluki;
        }

        /// <summary>
        /// The asset types Greyhound exports, i.e. the only ones an unresolved
        /// name has to agree with another tool on. Greyhound x-prefixes them
        /// (xmaterial_/ximage_/xmodel_/xanim_/xsound_), Saluki does not
        /// (material_/image_/model_/anim_/sound_); keyed on the Saluki
        /// spelling. A prefix that isn't in here (fx, klf, beam, weapon, …) is
        /// a type neither tool exports.
        /// </summary>
        private static readonly Dictionary<string, string> XPrefixes = new Dictionary<string, string>()
        {
            { "material", "xmaterial" },
            { "image",    "ximage"    },
            { "model",    "xmodel"    },
            { "anim",     "xanim"     },
            { "sound",    "xsound"    },
        };

        /// <summary>
        /// The style a given asset type is actually spelled in. The setting
        /// only reaches the types Greyhound exports — every other type is ours
        /// alone, so it always takes the Saluki spelling, which keeps the whole
        /// 63-bit name field instead of throwing bits 60-62 away.
        /// </summary>
        private static HashNameStyle StyleFor(string prefix)
        {
            return NameStyle == HashNameStyle.Greyhound && IsGreyhoundType(prefix) ?
                HashNameStyle.Greyhound :
                HashNameStyle.Saluki;
        }

        /// <summary>
        /// Is this a type Greyhound exports? Accepts either spelling of the
        /// prefix (our pool names are the x-prefixed ones).
        /// </summary>
        private static bool IsGreyhoundType(string prefix)
        {
            return XPrefixes.ContainsKey(prefix) || XPrefixes.ContainsValue(prefix);
        }

        /// <summary>
        /// Spells a type prefix in the given style: our pool names are the
        /// x-prefixed ones (PoolNames has "xmodel"/"xanim"), Greyhound's are
        /// too, Saluki's are bare.
        /// </summary>
        private static string StylePrefix(string prefix, HashNameStyle style)
        {
            if (style == HashNameStyle.Greyhound)
                return XPrefixes.TryGetValue(prefix, out var xprefix) ? xprefix : prefix;

            if (prefix.Length > 1 && prefix[0] == 'x' && XPrefixes.ContainsValue(prefix))
                return prefix.Substring(1);

            return prefix;
        }

        /// <summary>
        /// Hash -> asset name dictionary (loaded from optional files, see LoadHashIndex)
        /// </summary>
        public static Dictionary<ulong, string> HashIndex = new Dictionary<ulong, string>();

        /// <summary>
        /// T8 asset pool names by index (from ate47's acts games/bo4/pool.hpp)
        /// </summary>
        public static readonly string[] PoolNames =
        {
            "physpreset", "physconstraints", "destructibledef", "xanim", "xmodel",
            "xmodelmesh", "material", "computeshaderset", "techset", "image",
            "sound", "col_map", "com_map", "game_map", "gfx_map",
            "fonticon", "localizeentry", "localizelist", "gesture", "gesturetable",
            "weapon", "weaponfull", "weapontunables", "cgmediatable", "playersoundstable",
            "playerfxtable", "sharedweaponsounds", "attachment", "attachmentunique", "weaponcamo",
            "customizationtable", "customizationtable_feimages", "snddriverglobals", "fx", "tagfx",
            "klf", "impactsfxtable", "impactsoundstable", "aitype", "character",
            "xmodelalias", "rawfile", "animtree", "stringtable", "structuredtable",
            "leaderboarddef", "ddl", "glasses", "scriptparsetree", "scriptparsetreedbg",
            "scriptparsetreeforced", "keyvaluepairs", "vehicle", "tracer", "surfacefxtable",
            "surfacesounddef", "footsteptable", "entityfximpacts", "entitysoundimpacts", "zbarrier",
            "vehiclefxdef", "vehiclesounddef", "typeinfo", "scriptbundle", "scriptbundlelist",
            "rumble", "bulletpenetration", "locdmgtable", "aimtable", "shoottable",
            "playerglobaltunables", "animselectortableset", "animmappingtable", "animstatemachine", "behaviortree",
            "behaviorstatemachine", "ttf", "sanim", "lightdescription", "shellshock",
            "statuseffect", "cinematiccamera", "cinematicsequence", "spectatecamera", "xcam",
            "bgcache", "texturecombo", "flametable", "bitfield", "maptable",
            "maptablelist", "maptableloadingimages", "maptablepreviewimages", "maptableentrylevelassets", "objective",
            "objectivelist", "navmesh", "navvolume", "laser", "beam",
            "streamerhint", "flowgraph", "postfxbundle", "luafile", "luafiledebug",
            "renderoverridebundle", "staticlevelfxlist", "triggerlist", "playerroletemplate", "playerrolecategorytable",
            "playerrolecategory", "characterbodytype", "playeroutfit", "gametypetable", "feature",
            "featuretable", "unlockableitem", "unlockableitemtable", "entitylist", "playlists",
            "playlistglobalsettings", "playlistschedule", "motionmatchinginput", "blackboard", "tacticalquery",
            "playermovementtunables", "hierarchicaltasknetwork", "ragdoll", "storagefile", "storagefilelist",
            "charmixer", "storeproduct", "storecategory", "storecategorylist", "rank",
            "ranktable", "prestige", "prestigetable", "firstpartyentitlement", "firstpartyentitlementlist",
            "entitlement", "entitlementlist", "sku", "labelstore", "labelstorelist",
            "cpuocclusiondata", "lighting", "streamerworld", "talent", "playertalenttemplate",
            "playeranimation", "err_unused", "terraingfx", "highlightreelinfodefines", "highlightreelprofileweighting",
            "highlightreelstarlevels", "dlogevent", "rawstring", "ballisticdesc", "streamkey",
            "rendertargets", "drawnodes", "grouplodmodel", "fxlibraryvolume", "arenaseasons",
            "sprayorgestureitem", "sprayorgesturelist", "hwplatform",
        };

        /// <summary>
        /// Black Ops 4 Asset Pool Indices (T8 XAssetType — subset we care about)
        /// </summary>
        public enum AssetPool : int
        {
            xanim = 3,
            xmodel = 4,
            material = 6,
            image = 9,
            fx = 33,
            fxlibraryvolume = 163,
        }

        public string Name => "Black Ops 4";

        public string[] ProcessNames => new string[]
        {
            "blackops4"
        };

        public long AssetPoolsAddress { get; set; }

        public long BaseAddress { get; set; }

        public int ProcessIndex { get; set; }

        public List<IAssetPool> AssetPools { get; set; }

        public bool Initialize(HydraInstance instance)
        {
            NameStyle = ParseNameStyle(instance.Settings["HashNameStyle", "Saluki"]);

            var module = instance.Reader.Modules[0];
            var moduleBase = module.BaseAddress.ToInt64();

            var candidates = new List<long>();

            // Primary: Greyhound's DBAssetPools signature (trailing wildcards
            // dropped — FindBytes mishandles them and they add no constraint):
            //   48 89 5C 24 ?? 57 48 83 EC ?? 0F B6 F9 48 8D 05  -> RIP at +0x10
            try
            {
                var scan = instance.Reader.FindBytes(
                    new byte?[] { 0x48, 0x89, 0x5C, 0x24, null, 0x57, 0x48, 0x83, 0xEC, null, 0x0F, 0xB6, 0xF9, 0x48, 0x8D, 0x05 },
                    moduleBase,
                    moduleBase + module.Size,
                    true);

                foreach (var hit in scan)
                    candidates.Add(instance.Reader.ReadInt32(hit + 0x10) + hit + 0x14);
            }
            catch { }

            // Fallbacks: known per-build offsets
            candidates.Add(moduleBase + 0x912A5B0);  // final build (acts / Shield)
            candidates.Add(moduleBase + 0x889AD50);  // Greyhound-era build

            foreach (var candidate in candidates)
            {
                if (ValidatePoolsAddress(candidate, instance))
                {
                    AssetPoolsAddress = candidate;
                    BaseAddress = moduleBase;
                    LoadHashIndex();
                    BuildZoneRanges(instance);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Validates a candidate DBAssetPools address via the known first-xmodel
        /// name hash plus pool-info sanity checks
        /// </summary>
        private bool ValidatePoolsAddress(long address, HydraInstance instance)
        {
            try
            {
                var xmodelPool = instance.Reader.ReadStruct<AssetPoolInfo>(address + (int)AssetPool.xmodel * 0x20);

                if (!IsCanonicalPointer(xmodelPool.PoolPointer))
                    return false;
                if (xmodelPool.AssetSize <= 0 || xmodelPool.AssetSize > 0x40000)
                    return false;
                if (xmodelPool.PoolSize <= 0 || xmodelPool.PoolSize > 0x400000)
                    return false;

                var hash = (ulong)instance.Reader.ReadInt64(xmodelPool.PoolPointer) & HashMask;
                return hash == FirstXModelHash;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsCanonicalPointer(long value)
        {
            return value > 0x10000 && value < 0x7FFFFFFFFFFF;
        }

        /// <summary>
        /// A loaded zone's memory: the ranges its assets were streamed into
        /// </summary>
        private struct ZoneRange
        {
            public long Start;
            public long End;
            public string Name;
        }

        /// <summary>
        /// Zone memory ranges, in load order (see BuildZoneRanges)
        /// </summary>
        private List<ZoneRange> ZoneRanges = new List<ZoneRange>();

        /// <summary>
        /// Resolves the zone an asset was loaded from, by finding which zone's
        /// memory contains the asset's data.
        ///
        /// T8 has no per-asset zone index (T7's XAssetEntry pool is gone), so
        /// attribution is positional: g_zones holds, per loaded zone, the XBlock
        /// ranges the fastfile was streamed into, and every asset's data lives in
        /// exactly one of them. Pass the asset's DATA pointer (e.g. an fx's
        /// FxElements at +0x30), NOT its pool-slot header address - headers live
        /// in the pools, which are outside every zone range.
        /// </summary>
        public string GetZoneName(long dataPointer)
        {
            foreach (var zone in ZoneRanges)
                if (dataPointer >= zone.Start && dataPointer < zone.End)
                    return zone.Name;

            return "";
        }

        /// <summary>
        /// Reads g_zones into ZoneRanges. Layout (established live 2026-08-06):
        /// zone record stride 0x108, +0x00 zone id, +0x10 XBlock[9] of
        /// {void* data; size_t size; u64 pad}; the id indexes a 0x50-stride array
        /// of zone name strings. The loaded-zone count is its own dword.
        /// </summary>
        private void BuildZoneRanges(HydraInstance instance)
        {
            ZoneRanges = new List<ZoneRange>();

            try
            {
                var module = instance.Reader.Modules[0];
                var moduleBase = module.BaseAddress.ToInt64();

                // The zone-record indexing in the fastfile load path:
                //   movsxd rax, [rip+countAddress]
                //   imul   r14, rax, 0x108          <- zone record stride
                //   lea    rax, [rip+zonesAddress]
                //   add    r14, rax
                //   xor    edx, edx
                //   mov    r8d, 0x108
                var scan = instance.Reader.FindBytes(
                    new byte?[]
                    {
                        0x48, 0x63, 0x05, null, null, null, null,
                        0x4C, 0x69, 0xF0, 0x08, 0x01, 0x00, 0x00,
                        0x48, 0x8D, 0x05, null, null, null, null,
                        0x4C, 0x03, 0xF0, 0x33, 0xD2, 0x41, 0xB8, 0x08, 0x01, 0x00, 0x00
                    },
                    moduleBase,
                    moduleBase + module.Size,
                    true);

                if (scan.Length == 0)
                    return;

                var hit = scan[0];
                long countAddress = instance.Reader.ReadInt32(hit + 3) + hit + 7;
                long zonesAddress = instance.Reader.ReadInt32(hit + 17) + hit + 21;

                // The zone-name array is loaded a little further into the same
                // function: lea rdi, [rip+namesAddress]
                long namesAddress = 0;
                for (int i = 0x20; i < 0x120; i++)
                {
                    if (instance.Reader.ReadUInt16(hit + i) == 0x8D48
                        && instance.Reader.ReadBytes(hit + i + 2, 1)[0] == 0x3D)
                    {
                        namesAddress = instance.Reader.ReadInt32(hit + i + 3) + hit + i + 7;
                        break;
                    }
                }

                if (namesAddress == 0)
                    return;

                int zoneCount = instance.Reader.ReadInt32(countAddress);
                if (zoneCount <= 0 || zoneCount > 0x100)
                    return;

                for (int i = 0; i < zoneCount; i++)
                {
                    long record = zonesAddress + (long)i * 0x108;
                    int id = instance.Reader.ReadInt32(record);
                    if (id < 0 || id > 0x100)
                        continue;

                    var name = instance.Reader.ReadNullTerminatedString(namesAddress + (long)id * 0x50);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    for (int block = 0; block < 9; block++)
                    {
                        long data = instance.Reader.ReadInt64(record + 0x10 + block * 0x18);
                        long size = instance.Reader.ReadInt64(record + 0x10 + block * 0x18 + 8);

                        if (!IsCanonicalPointer(data) || size <= 0)
                            continue;

                        ZoneRanges.Add(new ZoneRange { Start = data, End = data + size, Name = name });
                    }
                }
            }
            catch
            {
                ZoneRanges = new List<ZoneRange>();
            }
        }

        /// <summary>
        /// Loads hash->name dictionaries from optional plain-text files next to the
        /// executable: hashes/*.txt and hashes/*.csv, lines "hash,name" or
        /// "hash name" (hash hex with or without 0x). Hashes are masked to 60 bits.
        /// A release's own dictionaries are read straight out of
        /// <see cref="FiggleUpdater.HashPayloadFolder"/> too, for the case where
        /// they could not be moved into hashes\ (a read-only install).
        /// </summary>
        public static void LoadHashIndex()
        {
            if (HashIndex.Count > 0)
                return;

            var root = AppDomain.CurrentDomain.BaseDirectory;

            // Shipped last: it is the newer copy of any file it shares a name with
            foreach (var dir in new[] { Path.Combine(root, "hashes"),
                                        Path.Combine(root, FiggleUpdater.HashPayloadFolder) })
                LoadHashDirectory(dir);
        }

        /// <summary>
        /// Adds every dictionary file in one folder to <see cref="HashIndex"/>
        /// </summary>
        private static void LoadHashDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;

                foreach (var file in Directory.GetFiles(dir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".txt" && ext != ".csv")
                        continue;

                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        var parts = line.Split(new[] { ',', ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 2)
                            continue;
                        var hex = parts[0].Trim();
                        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            hex = hex.Substring(2);
                        if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
                            HashIndex[hash & HashMask] = parts[1].Trim();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Resolves a name hash to a name, else spells the hash in the style
        /// that applies to this asset type (see <see cref="StyleFor"/>). Pass
        /// the field as stored (masked with <see cref="NameBits"/>, not
        /// <see cref="HashMask"/>) — the Saluki spelling needs bits 60-62,
        /// which the 60-bit mask throws away.
        /// </summary>
        public static string GetHashName(ulong hash, string prefix)
        {
            hash &= NameBits;

            if (HashIndex.TryGetValue(hash & HashMask, out var name))
                return name;

            var style = StyleFor(prefix);

            if (style == HashNameStyle.Greyhound)
                return string.Format("{0}_{1:x}", StylePrefix(prefix, style), hash & HashMask);

            return string.Format("{0}_{1:x16}", StylePrefix(prefix, style), hash);
        }

        /// <summary>
        /// FNV1a-64 of the given string, masked to 60 bits (T8 asset name hashing)
        /// </summary>
        public static ulong HashName(string value)
        {
            ulong hash = 0xCBF29CE484222325;
            foreach (char c in value.ToLowerInvariant())
            {
                hash ^= (byte)c;
                hash *= 0x100000001B3;
            }
            return hash & HashMask;
        }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }
}
