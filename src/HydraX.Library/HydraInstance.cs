using PhilLibX.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HydraX.Library
{
    /// <summary>
    /// A Class to hold an Instance of the FiggleFX Library
    /// </summary>
    public class HydraInstance
    {
        /// <summary>
        /// Gets or Sets the List of Supported Games
        /// </summary>
        public List<IGame> Games { get; set; }

        /// <summary>
        /// Gets or Sets the current Game
        /// </summary>
        public IGame Game { get; set; }

        /// <summary>
        /// Gets or Sets the current Process Reader
        /// </summary>
        public ProcessReader Reader { get; set; }

        /// <summary>
        /// Gets or Sets the current Settings
        /// </summary>
        public HydraSettings Settings = new HydraSettings();

        /// <summary>
        /// Gets or Sets the loaded Assets
        /// </summary>
        public List<Asset> Assets { get; set; }

        /// <summary>
        /// Gets the Export Path
        /// </summary>
        public string ExportFolder { get { return Path.Combine("exported_files", Game.Name); } }

        /// <summary>
        /// Initializes Supported Games
        /// </summary>
        public void Initialize()
        {
            Games = new List<IGame>();
            var gameType = typeof(IGame);
            var games = AppDomain.CurrentDomain.GetAssemblies().SelectMany(s => s.GetTypes()).Where(p => gameType.IsAssignableFrom(p));

            foreach (var game in games)
                if (!game.IsInterface)
                    Games.Add((IGame)Activator.CreateInstance(game));
        }

        /// <summary>
        /// Gets Asset Pools for the given Game
        /// </summary>
        /// <remarks>
        /// Pools are found by reflection: any <see cref="IAssetPool"/> nested
        /// inside the game's class belongs to that game.
        /// </remarks>
        public static List<IAssetPool> GetAssetPools(IGame game)
        {
            var poolType = typeof(IAssetPool);
            var results = new List<IAssetPool>();

            var pools = AppDomain.CurrentDomain.GetAssemblies().SelectMany(s => s.GetTypes()).Where(p => poolType.IsAssignableFrom(p));

            foreach (var pool in pools)
                if (!pool.IsInterface)
                    if (pool.DeclaringType is Type gameType)
                        if (gameType == game.GetType())
                            results.Add((IAssetPool)Activator.CreateInstance(pool));

            return results;
        }

        public void Clear()
        {
            Game = null;
            Reader = null;
            Assets = null;
        }

        public HydraInstance()
        {
            Initialize();
        }

        public void LoadGame()
        {
            Assets = new List<Asset>();

            Process[] processes = Process.GetProcesses();

            foreach (var process in processes)
            {
                foreach (var game in Games)
                {
                    for (int i = 0; i < game.ProcessNames.Length; i++)
                    {
                        if (process.ProcessName.ToLower() == game.ProcessNames[i].ToLower())
                        {
                            Game = (IGame)game.Clone();
                            Game.ProcessIndex = i;
                            Reader = new ProcessReader(process);

                            if (Game.Initialize(this))
                            {
                                Game.AssetPools = GetAssetPools(Game);

                                foreach (var assetPool in Game.AssetPools)
                                    Assets.AddRange(assetPool.Load(this));

                                return;
                            }
                            else
                            {
                                Clear();
                                throw new GameNotSupportedException(game.Name);
                            }
                        }
                    }
                }
            }

            throw new GameNotFoundException();
        }
    }
}
