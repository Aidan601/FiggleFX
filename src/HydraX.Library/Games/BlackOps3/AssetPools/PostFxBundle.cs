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
        /// Black Ops 3 PostFX Bundle Logic
        ///
        /// postfxbundle has no asset pool of its own in T7 — it is a script
        /// bundle (scriptbundle pool, type string "postfxbundle") holding the
        /// raw GDT key/value pairs from postfxbundle.gdf (deffiles/
        /// postfxbundle.awi + gfx_bundle.h). This pool surfaces those bundles
        /// as their own asset type and writes one shipping-format GDT per
        /// bundle, plus the referenced-material list. Standalone by design so
        /// it ports to the FiggleFX tree, which has no GameDataTable.
        /// </summary>
        private class PostFxBundle : IAssetPool
        {
            /// <summary>
            /// Bundle keys whose shipping GDT spelling is not all-lowercase
            /// (compiled script strings are lowercased). Stage-local names are
            /// matched against the part after the "sNN_" prefix.
            /// </summary>
            private static readonly Dictionary<string, string> KeyCase = new Dictionary<string, string>()
            {
                { "configstringfiletype", "configstringFileType" },
                { "vmtype",               "vmType" },
                { "enterstage",           "enterStage" },
                { "exitstage",            "exitStage" },
                { "finishlooponexit",     "finishLoopOnExit" },
                { "screencapture",        "screenCapture" },
                { "spritefilter",         "spriteFilter" },
            };

            /// <summary>
            /// Size of each asset
            /// </summary>
            public int AssetSize { get; set; }

            /// <summary>
            /// Gets or Sets the number of Assets
            /// </summary>
            public int AssetCount { get; set; }

            /// <summary>
            /// Gets or Sets the Start Address
            /// </summary>
            public long StartAddress { get; set; }

            /// <summary>
            /// Gets or Sets the End Address
            /// </summary>
            public long EndAddress { get { return StartAddress + (AssetCount * AssetSize); } set => throw new NotImplementedException(); }

            /// <summary>
            /// Gets the Name of this Pool
            /// </summary>
            public string Name => "postfxbundle";

            /// <summary>
            /// Gets the Setting Group for this Pool
            /// </summary>
            public string SettingGroup => "PostFxBundle";

            /// <summary>
            /// Gets the Index of this Pool
            /// </summary>
            public int Index => (int)AssetPool.scriptbundle;

            /// <summary>
            /// Reads the bundle's KVPs (raw lowercased key -> value string;
            /// float values re-formatted APE-style: "1.0" -> "1"). KVP block
            /// stride 0x18: +0 key string index, +4 value string index,
            /// +8 value type (2 = float), +C raw value, +10 pointer.
            /// </summary>
            private static Dictionary<string, string> ReadProperties(long address, HydraInstance instance)
            {
                var properties = new Dictionary<string, string>();

                int propertyCount = instance.Reader.ReadInt32(address + 0x10);
                long propertiesPointer = instance.Reader.ReadInt64(address + 0x18);
                if (propertyCount <= 0 || propertiesPointer == 0)
                    return properties;

                var buffer = instance.Reader.ReadBytes(propertiesPointer, propertyCount * 0x18);

                for (int i = 0; i < propertyCount; i++)
                {
                    var value = instance.Game.GetString(BitConverter.ToInt32(buffer, i * 0x18 + 4), instance);

                    if (BitConverter.ToInt32(buffer, i * 0x18 + 8) == 2 &&
                        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        value = number.ToString("0.######", CultureInfo.InvariantCulture);

                    properties[instance.Game.GetString(BitConverter.ToInt32(buffer, i * 0x18), instance)] = value;
                }

                return properties;
            }

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

                    if (instance.Game.GetString(instance.Reader.ReadInt32(address + 8), instance) != "postfxbundle")
                        continue;

                    var properties = ReadProperties(address, instance);
                    properties.TryGetValue("num_stages", out var stages);
                    bool looping = properties.TryGetValue("looping", out var loop) && loop == "1";

                    results.Add(new Asset()
                    {
                        Name        = instance.Reader.ReadNullTerminatedString(namePointer),
                        Type        = Name,
                        Zone        = ((BlackOps3)instance.Game).ZoneNames.TryGetValue(address, out var zone) ? zone : "unknown",
                        Information = string.Format("{0} Stage{1}{2}", stages ?? "?", stages == "1" ? "" : "s", looping ? ", Looping" : ""),
                        Status      = "Loaded",
                        Data        = address,
                        LoadMethod  = ExportAsset,
                    });
                }

                return results;
            }

            /// <summary>
            /// Exports the given bundle as a shipping-format postfxbundle.gdf GDT
            /// </summary>
            public void ExportAsset(Asset asset, HydraInstance instance)
            {
                var address = (long)asset.Data;

                if (asset.Name != instance.Reader.ReadNullTerminatedString(instance.Reader.ReadInt64(address)))
                    throw new Exception("The asset at the expected address has changed. Press the Load Game button to refresh the asset list.");

                var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var assets = new SortedSet<string>(StringComparer.Ordinal);

                foreach (var property in ReadProperties(address, instance))
                {
                    var key = property.Key;
                    var value = property.Value;

                    // linker-injected KVPs APE never writes back
                    if (key == "name" || key == "igdtseqnum")
                        continue;

                    // restore APE key casing (compiled script strings are lowercased)
                    var stagePrefix = "";
                    var local = key;
                    if (key.Length > 4 && key[0] == 's' && key[3] == '_' && char.IsDigit(key[1]) && char.IsDigit(key[2]))
                    {
                        stagePrefix = key.Substring(0, 4);
                        local = key.Substring(4);
                    }
                    if (KeyCase.TryGetValue(local, out var cased))
                        key = stagePrefix + cased;

                    // restore value casing
                    value = value.Replace("scriptvector", "scriptVector");
                    if (key == "configstringFileType")
                        value = value.ToUpperInvariant();
                    else if (key == "vmType" && value.Length > 0)
                        value = char.ToUpperInvariant(value[0]) + value.Substring(1);
                    else if (key.EndsWith("_material") && value.Length > 0)
                        assets.Add("material " + value);

                    values[key] = value;
                }

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
                File.WriteAllText(Path.Combine(dir, asset.Name + ".gdt"), sb.ToString());

                if (instance.Settings["ExportAssetList", "Yes"] == "Yes")
                    File.WriteAllText(Path.Combine(dir, asset.Name + "_assets.txt"),
                                      FXEffect.AssetList(asset.Name, assets));
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
