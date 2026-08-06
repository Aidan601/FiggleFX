using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HydraX.Library
{
    /// <summary>
    /// Black Ops 3 (T7) support.
    ///
    /// Attaches to "blackops3.exe" (Steam or Windows Store) and exposes the
    /// asset pool table plus the per-asset zone names the fx and lens flare
    /// pools list their assets with.
    /// </summary>
    public partial class BlackOps3 : IGame
    {
        #region Structures
        /// <summary>
        /// Asset Pool Data
        /// </summary>
        public struct AssetPoolInfo
        {
            #region AssetPoolInfoProperties
            public long PoolPointer { get; set; }
            public int AssetSize { get; set; }
            public int PoolSize { get; set; }
            public int Padding { get; set; }
            public int AssetCount { get; set; }
            public long FreeSlot { get; set; }
            #endregion
        }
        #endregion

        /// <summary>
        /// Gets Black Ops 3's Game Name
        /// </summary>
        public string Name => "Black Ops III";

        /// <summary>
        /// Gets Black Ops 3's Process Names
        /// </summary>
        public string[] ProcessNames => new string[]
        {
            "blackops3"
        };

        /// <summary>
        /// Gets Black Ops 3 Asset Pools Addresses
        /// </summary>
        public long AssetPoolsAddress { get; set; }

        /// <summary>
        /// Gets or Sets Black Ops 3's Base Address (ASLR)
        /// </summary>
        public long BaseAddress { get; set; }

        /// <summary>
        /// Gets or Sets the current Process Index
        /// </summary>
        public int ProcessIndex { get; set; }

        /// <summary>
        /// Gets or sets the list of Asset Pools
        /// </summary>
        public List<IAssetPool> AssetPools { get; set; }

        /// <summary>
        /// Gets or Sets the Zone Names by asset header address
        /// </summary>
        public Dictionary<long, string> ZoneNames { get; set; }

        /// <summary>
        /// Black Ops III Asset Pool Indices (T7 XAssetType ordering — the pool
        /// table is indexed by these, so the full list has to stay in order)
        /// </summary>
        internal enum AssetPool : int
        {
            physpreset,
            physconstraints,
            destructibledef,
            xanim,
            xmodel,
            xmodelmesh,
            material,
            computeshaderset,
            techset,
            image,
            sound,
            sound_patch,
            col_map,
            com_map,
            game_map,
            map_ents,
            gfx_map,
            lightdef,
            lensflaredef,
            ui_map,
            font,
            fonticon,
            localize,
            weapon,
            weapondef,
            weaponvariant,
            weaponfull,
            cgmediatable,
            playersoundstable,
            playerfxtable,
            sharedweaponsounds,
            attachment,
            attachmentunique,
            weaponcamo,
            customizationtable,
            customizationtable_feimages,
            customizationtablecolor,
            snddriverglobals,
            fx,
            tagfx,
            klf,
            impactsfxtable,
            impactsoundstable,
            player_character,
            aitype,
            character,
            xmodelalias,
            rawfile,
            stringtable,
            structuredtable,
            leaderboarddef,
            ddl,
            glasses,
            texturelist,
            scriptparsetree,
            keyvaluepairs,
            vehicle,
            addon_map_ents,
            tracer,
            slug,
            surfacefxtable,
            surfacesounddef,
            footsteptable,
            entityfximpacts,
            entitysoundimpacts,
            zbarrier,
            vehiclefxdef,
            vehiclesounddef,
            typeinfo,
            scriptbundle,
            scriptbundlelist,
            rumble,
            bulletpenetration,
            locdmgtable,
            aimtable,
            animselectortable,
            animmappingtable,
            animstatemachine,
            behaviortree,
            behaviorstatemachine,
            ttf,
            sanim,
            lightdescription,
            shellshock,
            xcam,
            bgcache,
            texturecombo,
            flametable,
            bitfield,
            attachmentcosmeticvariant,
            maptable,
            maptableloadingimages,
            medal,
            medaltable,
            objective,
            objectivelist,
            umbra_tome,
            navmesh,
            navvolume,
            binaryhtml,
            laser,
            beam,
            streamerhint,
            _string,
            assetlist,
            report,
            depend,
        }

        public object Clone()
        {
            return MemberwiseClone();
        }

        /// <summary>
        /// T7's per-asset zone record — this is what gives every asset its zone
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 8, Size = 0x20)]
        struct XAssetEntryPoolEntry
        {
            public AssetPool AssetType;
            public long HeaderPointer;
            public byte ZoneIndex;
        }

        public bool Initialize(HydraInstance instance)
        {
            var module = instance.Reader.Modules[0];
            bool isWindowsStore = instance.Reader.ReadInt16(module.BaseAddress.ToInt64() + 0x3C) == 0x1A0; // NOTE(serious): only works for a specific windows store executable (initial release -> current as of Jan 2025)

            long zoneEntriesAddress;
            XAssetEntryPoolEntry[] poolEntries;
            if (!isWindowsStore)
            {
                long[] pools, poolEntrys;
                pools = instance.Reader.FindBytes(
                new byte?[] { 0x63, 0xC1, 0x48, 0x8D, 0x05, null, null, null, null, 0x49, 0xC1, 0xE0, null, 0x4C, 0x03, 0xC0 },
                module.BaseAddress.ToInt64(),
                module.BaseAddress.ToInt64() + module.Size,
                true);

                poolEntrys = instance.Reader.FindBytes(
                    new byte?[] { 0x48, 0x8D, 0x05, null, null, null, null, 0x41, 0x8B, 0x34, 0x24, 0x85, 0xF6, 0x0F, 0x84, 0xF0, 0x00, 0x00, 0x00, 0x4C, 0x8D },
                    module.BaseAddress.ToInt64(),
                    module.BaseAddress.ToInt64() + module.Size,
                    true);

                if (pools.Length == 0 || poolEntrys.Length == 0)
                    return false;

                AssetPoolsAddress = instance.Reader.ReadInt32(pools[0] + 5) + pools[0] + 9;
                BaseAddress = module.BaseAddress.ToInt64();

                zoneEntriesAddress = instance.Reader.ReadInt32(poolEntrys[0] + 22) + poolEntrys[0] + 26;

                // Max of 156672 as per listassetpool
                poolEntries = instance.Reader.ReadArrayUnsafe<XAssetEntryPoolEntry>(instance.Reader.ReadInt32(poolEntrys[0] + 3) + poolEntrys[0] + 7, 156672);
            }
            else
            {
                // NOTE(serious): has hardcoded addresses for specific windows store executable -- pattern scanning is still possible but many functions get inlined/messed with, and I don't expect the windows store version to be updated with anything substantial.
                //                just in case, I provided signatures to find the same code sections, but the rel offsets will be different for calculating the final addresses, so if this becomes an issue those will have to be messed with.

                // 48 8B CD 48 8B 6C 24 ? 48 C1 E1 05 42 80 BC 21 ? ? ? ? ? 75 1B 4A 8B 84 21 ? ? ? ? 48 89 03 42 FF 8C 21 ? ? ? ? 4A 89 9C 21 ? ? ? ?
                AssetPoolsAddress = 0xF3B0C70L + module.BaseAddress.ToInt64();
                BaseAddress = module.BaseAddress.ToInt64();

                // 48 8D 05 ? ? ? ? 66 66 0F 1F 84 00 ? ? ? ? 41 8B 34 24 85 F6 0F 84 ? ? ? ? 4C 8D 2D ? ? ? ? 4C 8D 25 ? ? ? ? 66 0F 1F 44 00 ?
                zoneEntriesAddress = 0xF882300L + module.BaseAddress.ToInt64();

                // Max of 156672 as per listassetpool
                poolEntries = instance.Reader.ReadArrayUnsafe<XAssetEntryPoolEntry>(0xF3BA2F0L + module.BaseAddress.ToInt64(), 156672);
            }

            // Store zone names by asset pointer
            var zones = new string[65];
            zones[0] = "default_zone";
            for (int i = 1; i < zones.Length; i++)
            {
                zones[i] = instance.Reader.ReadNullTerminatedString(zoneEntriesAddress + 96 * i);

                if (string.IsNullOrWhiteSpace(zones[i]))
                    zones[i] = "unknown";
            }

            ZoneNames = new Dictionary<long, string>();

            foreach (var poolEntry in poolEntries)
            {
                if (poolEntry.HeaderPointer != 0) // Validate if this is not an empty slot (same as pools, points to next, but we can check Header Pointer)
                    ZoneNames[poolEntry.HeaderPointer] = zones[poolEntry.ZoneIndex];
            }

            return true;
        }
    }
}
