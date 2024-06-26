﻿/*
BUILD INFORMATION

This project uses ManagedBlam.dll, but that can't be added to the GitHub repo.
In order to still be built, the project uses a Reference Assembly version
of the ManagedBlam DLL. This is generated using the NetBrains tool Refasmer.

In theory it already contains all namespaces/methods etc present in the full DLL.
In case it needs to be regenerated in future howerver:

Refasmer can be installed from the terminal with "dotnet tool install -g JetBrains.Refasmer.CliTool"
Once installed, run with "refasmer -v -O ref -c ManagedBlam.dll"

When you need to debug this code, you need to switch the project reference to the "full" .dll or it
will crash at runtime. Don't forget to revert the reference back to version in the "ref" folder
before committing or release.
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OsoyoosMB
{
    public class MBHandler
    {
        // random ID for MBHandler
        public const string command_id = "70512702-FBD6-400F-8398-E96D8EB3D802";
        /// <summary>
        /// Load assemblies from the bin folder in the working directory
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private static Assembly LoadFromBinFolder(object sender, ResolveEventArgs args)
        {
            AssemblyName assemblyName = new AssemblyName(args.Name);
            Debug.Write($"{assemblyName}");

            string currentPath = Directory.GetCurrentDirectory();
            string assemblyPath = Path.Join(currentPath, "bin", assemblyName.Name) + ".dll";

            if (File.Exists(assemblyPath))
            {
                Assembly assembly = Assembly.LoadFrom(assemblyPath);
                return assembly;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Preload the managed blam assembly
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PreloadManagedBlam()
        {
            string currentPath = Directory.GetCurrentDirectory();
            string binManagedBlamPath = Path.Join(currentPath, "bin", "ManagedBlam.dll");
            try
            {
                Assembly.LoadFile(binManagedBlamPath);
                return true;
            } catch (FileNotFoundException)
            {
                // bad times
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Premain(String[] args)
        {
            try
            {
                // premain setup
                AppDomain currentDomain = AppDomain.CurrentDomain;
                currentDomain.AssemblyResolve += new ResolveEventHandler(LoadFromBinFolder);
                if (!PreloadManagedBlam())
                {
                    return -2;
                }

                return RunCommands(args);
            }
            catch (Exception)
            {
                return -3;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunCommands(String[] args)
        {
            if (args[0] == "setup_bitmap_compression" && args.Length == 5)
            {
                Console.WriteLine("Running setup_bitmap_compression");
                BitmapSettings.ConfigureCompression(args[1], args[2], args[3], int.Parse(args[4]));
                return 1;
            }
            else
            {
                return -1;
            }
        }
    }
}
