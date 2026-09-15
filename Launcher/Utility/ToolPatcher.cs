using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using ToolkitLauncher.ToolkitInterface;

namespace ToolkitLauncher.Utility
{
    internal class ToolPatcher
    {
        /// <summary>
        /// Reads byte regions centered at the specified offset(s) from a file, concatenates them,
        /// and computes a SHA-256 hash.
        /// </summary>
        /// <param name="exePath">The full file path to the executable/binary file to read.</param>
        /// <param name="offsets">Collection of byte offsets in the file where each target region is centered.</param>
        /// <param name="regionSize">The total size, in bytes, to read for each region around its offset.</param>
        /// <returns>A hex string of the SHA-256 hash of the combined region bytes.</returns>
        static string ComputeRegionHash(string exePath, IEnumerable<long> offsets, int regionSize)
        {
            using var sha256 = SHA256.Create();
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read);
            using var ms = new MemoryStream();

            // Gather all region bytes together
            foreach (var offset in offsets)
            {
                long regionStart = Math.Max(0, offset - regionSize / 2); // Center region on the offset
                byte[] buffer = new byte[regionSize];

                fs.Seek(regionStart, SeekOrigin.Begin);
                int bytesRead = fs.Read(buffer, 0, buffer.Length);

                ms.Write(buffer, 0, bytesRead);
            }

            // Hash the joined regions
            byte[] hash = sha256.ComputeHash(ms.ToArray());
            return BitConverter.ToString(hash).Replace("-", "");
        }

        /// <summary>
        /// Opens the target executable and writes byte patches to the specified file offsets
        /// </summary>
        /// <param name="exePath">The file path to the target executable.</param>
        /// <param name="patches">A collection of tuples containing the target file offset original, and replacement byte sequences.</param>
        static void PatchTool(string exePath, IEnumerable<(long offset, byte[] bytes)> patches)
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            foreach (var (offset, bytes) in patches)
            {
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Determines whether a patching or reverting operation should proceed by validating the file's current hash 
        /// against the expected pre-calculated original and patched hash values.
        /// </summary>
        /// <param name="fileHash">The current computed region hash of the target file.</param>
        /// <param name="originalHash">The expected region hash of the unpatched original file.</param>
        /// <param name="patchedHash">The expected region hash of the modified file.</param>
        /// <param name="exeName">The name of the executable for logging/error outputs.</param>
        /// <param name="patchName">A friendly name of the patch for logging/error outputs.</param>
        /// <param name="applyPatch"><c>true</c> if checking readiness to apply the patch; <c>false</c> if checking readiness to revert it.</param>
        /// <returns>
        /// <c>true</c> if the file is in the expected state for modification; <c>false</c> if the file is already in the target state or if an unknown hash mismatch occurs.
        /// </returns>
        static bool ShouldPatch(string fileHash, string originalHash, string patchedHash, string exeName, string patchName, bool applyPatch)
        {
            if (applyPatch)
            {
                if (fileHash.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // Safe to patch original file
                }

                if (fileHash.Equals(patchedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Already patched, skip
                }
            }
            else
            {
                if (fileHash.Equals(patchedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // Safe to revert
                }

                if (fileHash.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Already unpatched
                }
            }

            // Unknown file changes
            Trace.WriteLine($"Region hash mismatch for {exeName} -- aborting {patchName} patch");
            MessageBox.Show($"Region hash mismatch for {exeName} -- aborting {patchName} patch.\nUnknown modification detected in the 1KB surrounding patch region(s).\nIf you don't know what this means, contact Crisp or PepperMan on Discord.",
            "Patcher Error", MessageBoxButton.OK, MessageBoxImage.Error
            );
            return false;
        }

        /// <summary>
        /// Disables "color->red>=0" type assertion failures during lightmapping.
        /// </summary>
        /// <param name="applyPatch"><c>true</c> writes the patch bytes; <c>false</c> reverts to the original file data.</param>
        /// <param name="toolPath">The file path to tool.exe</param>
        /// <param name="toolFastPath">The file path to tool_fast.exe</param>
        // 
        public static void PatchLightmapColorAssert(bool applyPatch, string toolPath, string toolFastPath)
        {
            byte[] newBytes = [0x90, 0x90, 0x90, 0x90, 0x90];

            var toolPaths = new[]
            {
                (Type: ToolType.Tool, Path: toolPath),
                (Type: ToolType.ToolFast, Path: toolFastPath)
            };

            // Loop through both exe types
            foreach (var (toolType, exePath) in toolPaths)
            {
                if (!File.Exists(exePath))
                {
                    continue;
                }

                // Verify hash before patching
                var patchData = reachLightmapColorPatchData[toolType];
                string fileHash = ComputeRegionHash(exePath, patchData.locations.Select(x => x.offset), 1024);

                // Exit early if patch shouldn't be applied
                if (!ShouldPatch(fileHash, patchData.original, patchData.patched, Path.GetFileName(exePath), "Reach Lightmap Color", applyPatch))
                {
                    continue;
                }

                // Perform patching/reverting
                try
                {
                    PatchTool(exePath, patchData.locations.Select(loc => (loc.offset, applyPatch ? newBytes : loc.RevertBytes)));
                }
                catch (IOException ex)
                {
                    MessageBox.Show($"Failed to patch {exePath}: {ex.Message}", "Patch Failure", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Applies multiple small patches that stop Tool from corrupting the FMOD bank all the time
        /// apparently this stems from the code incorrectly reading the old, existing FMOD bank data using the NEW
        /// sound count, thus running past the existing sound headers, reading garbage/audio data as headers, and
        /// throwing errors and breaking things.
        /// </summary>
        /// <param name="applyPatch"><c>true</c> writes the patch bytes; <c>false</c> reverts to the original file data.</param>
        /// <param name="toolPath">The file path to tool.exe</param>
        /// <param name="toolFastPath">The file path to tool_fast.exe</param>
        public static void PatchFSBImportFixes(bool applyPatch, string toolPath, string toolFastPath, string engine)
        {
            (string original, string patched, (long offset, byte[] OriginalBytes, byte[] PatchBytes)[] locations) patchData;

            switch (engine)
            {
                case "H3":
                    patchData = h3FmodPatchData[0];
                    break;
                case "ODST":
                    patchData = odstFmodPatchData[0];
                    break;
                case "Reach":
                    patchData = reachFmodPatchData[0];
                    break;
                default:
                    Console.WriteLine("No FMOD fix patch data defined for this engine!!");
                    return;
            }

            string fileHash = ComputeRegionHash(toolPath, patchData.locations.Select(x => x.offset), 1024);
            Console.WriteLine(fileHash);

            // Exit early if patch shouldn't be applied
            if (!ShouldPatch(fileHash, patchData.original, patchData.patched, Path.GetFileName(toolPath), "FMOD Import", applyPatch))
            {
                return;
            }

            // Perform patching/reverting
            try
            {
                PatchTool(toolPath, patchData.locations.Select(loc => (loc.offset, applyPatch ? loc.PatchBytes : loc.OriginalBytes)));
            }
            catch (IOException ex)
            {
                MessageBox.Show($"Failed to patch {toolPath}: {ex.Message}", "Patch Failure", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        // Patch for fixing FMOD import errors
        // For Reach tool.exe version 18/07/2023
        private static readonly Dictionary<ToolType, (string original, string patched, (long offset, byte[] OriginalBytes, byte[] PatchBytes)[] locations)> reachFmodPatchData = new()
        {
            {
                ToolType.Tool,
                (
                    "A81F6726BC7DF3F414B5B740A494F1961D0357E5BE1FD2DACC8062DB0608BCED", // original
                    "2FF1EB5692BB58862E12F848A2DC9B1A8CE68F37F9BAE07D7BBB45DA5E860052", // patched
                    new (long, byte[], byte[])[]
                    {
                        ( 0x3CD5B2, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0x44, 0x8B, 0x7C, 0xC8, 0x08, 0x48, 0x8B, 0x44, 0x24, 0x78, 0xEB, 0x44 }),
                        ( 0x3CD602, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0x8B, 0x40, 0x08, 0xFF, 0xC8, 0x41, 0x39, 0xC7, 0x44, 0x0F, 0x42, 0xF8, 0xC3 }),
                        ( 0x3CECBE, new byte[] { 0xFF, 0xC0, 0x89, 0x46, 0x70 }, new byte[] { 0xE8, 0x20, 0x02, 0x00, 0x00 }),
                        ( 0x3CEEE3, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC0, 0x3B, 0x45, 0xD7, 0x0F, 0x42, 0x45, 0xD7, 0x89, 0x46, 0x70, 0xC3 }),
                        ( 0x3CF0DF, new byte[] { 0xFF, 0xC7, 0x83, 0xC0, 0x3C }, new byte[] { 0xE8, 0x9D, 0x24, 0x00, 0x00 }),
                        ( 0x3CFD42, new byte[] { 0xFF, 0xC7, 0x48, 0x89, 0x5C, 0x24, 0x70 }, new byte[] { 0xE8, 0x0A, 0x16, 0x00, 0x00, 0x90, 0x90 }),
                        ( 0x3D0415, new byte[] { 0x44, 0x8B, 0x7C, 0xC8, 0x08 }, new byte[] { 0xE8, 0x98, 0xD1, 0xFF, 0xFF }),
                        ( 0x3D0473, new byte[] { 0x0F, 0x1F, 0x40, 0x00 }, new byte[] { 0xEB, 0x0B, 0xEB, 0xB9 }),
                        ( 0x3D04E5, new byte[] { 0x3B, 0x74, 0x24, 0x48, 0x0F, 0x82, 0x41, 0xFF, 0xFF, 0xFF }, new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x70, 0x3B, 0x70, 0x08, 0x72, 0x86 }),
                        ( 0x3D1351, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC7, 0x3B, 0x7E, 0x08, 0x0F, 0x42, 0x7E, 0x08, 0x48, 0x89, 0x5C, 0x24, 0x78, 0xC3 }),
                        ( 0x3D1581, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC7, 0x41, 0x3B, 0x79, 0x08, 0x41, 0x0F, 0x42, 0x79, 0x08, 0x83, 0xC0, 0x3C, 0xC3 })
                    }
                )
            },
        };

        // Patch for fixing FMOD import errors
        // For H3 tool.exe version 12/01/2024
        private static readonly Dictionary<ToolType, (string original, string patched, (long offset, byte[] OriginalBytes, byte[] PatchBytes)[] locations)> h3FmodPatchData = new()
        {
            {
                ToolType.Tool,
                (
                    "652522E4A098F5B9DFA18138AB8F038726C42006F0CF7C3B8674B0B77ED84D51", // original
                    "7F80A02178FD9CABE18A1736DD08013B36C6385180FD1E81D9CD1A42ABD59771", // patched
                    new (long, byte[], byte[])[]
                    {
                        ( 0x26D123, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC0, 0x3B, 0x45, 0xD7, 0x0F, 0x42, 0x45, 0xD7, 0x89, 0x46, 0x70, 0xC3 }),
                        ( 0x26D982, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0x44, 0x8B, 0x7C, 0xC8, 0x08, 0x48, 0x8B, 0x44, 0x24, 0x78, 0xEB, 0x24 }),
                        ( 0x26D9B2, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0x8B, 0x40, 0x08, 0xFF, 0xC8, 0x41, 0x39, 0xC7, 0x44, 0x0F, 0x42, 0xF8, 0xC3 }),
                        ( 0x26F08E, new byte[] { 0xFF, 0xC0, 0x89, 0x46, 0x70 }, new byte[] { 0xE8, 0x90, 0xE0, 0xFF, 0xFF }),
                        ( 0x26F2CF, new byte[] { 0xFF, 0xC7, 0x83, 0xC0, 0x3C }, new byte[] { 0xE8, 0x6D, 0x22, 0x00, 0x00 }),
                        ( 0x26FF32, new byte[] { 0xFF, 0xC7, 0x48, 0x89, 0x5C, 0x24, 0x70 }, new byte[] { 0xE8, 0xDA, 0x13, 0x00, 0x00, 0x90, 0x90 }),
                        ( 0x270605, new byte[] { 0x44, 0x8B, 0x7C, 0xC8, 0x08 }, new byte[] { 0xE8, 0x78, 0xD3, 0xFF, 0xFF }),
                        ( 0x270663, new byte[] { 0x0F, 0x1F }, new byte[] { 0xEB, 0x0B }),
                        ( 0x270665, new byte[] { 0x40, 0x00 }, new byte[] { 0xEB, 0xB9 }),
                        ( 0x2706D5, new byte[] { 0x3B, 0x74, 0x24, 0x48, 0x0F, 0x82, 0x41, 0xFF, 0xFF, 0xFF }, new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x70, 0x3B, 0x70, 0x08, 0x72, 0x86 }),
                        ( 0x271311, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC7, 0x3B, 0x7E, 0x08, 0x0F, 0x42, 0x7E, 0x08, 0x48, 0x89, 0x5C, 0x24, 0x78, 0xC3 }),
                        ( 0x271541, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC7, 0x41, 0x3B, 0x79, 0x08, 0x41, 0x0F, 0x42, 0x79, 0x08, 0x83, 0xC0, 0x3C, 0xC3 })
                    }
                )
            },
        };

        // Patch for fixing FMOD import errors
        // For ODST tool.exe version 08/09/2023
        private static readonly Dictionary<ToolType, (string original, string patched, (long offset, byte[] OriginalBytes, byte[] PatchBytes)[] locations)> odstFmodPatchData = new()
        {
            {
                ToolType.Tool,
                (
                    "D35211F91BE1DFA69A159536BEB7DC938026CC2CEA51C3B3B03CF24D71D5AB48", // original
                    "F4C2005A1C79B6A615C90EC98F47BA364692E826C5BC7B48637CD08138D5356A", // patched
                    new (long, byte[], byte[])[]
                    {
                        ( 0x278373, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC0, 0x3B, 0x45, 0xD7, 0x0F, 0x42, 0x45, 0xD7, 0x89, 0x46, 0x70, 0xC3 }),
                        ( 0x278BD2, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0x44, 0x8B, 0x7C, 0xC8, 0x08, 0x48, 0x8B, 0x44, 0x24, 0x78, 0xEB, 0x24 }),
                        ( 0x278C02, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0x8B, 0x40, 0x08, 0xFF, 0xC8, 0x41, 0x39, 0xC7, 0x44, 0x0F, 0x42, 0xF8, 0xC3 }),
                        ( 0x27A2DE, new byte[] { 0xFF, 0xC0, 0x89, 0x46, 0x70 }, new byte[] { 0xE8, 0x90, 0xE0, 0xFF, 0xFF }),
                        ( 0x27A51F, new byte[] { 0xFF, 0xC7, 0x83, 0xC0, 0x3C }, new byte[] { 0xE8, 0x6D, 0x22, 0x00, 0x00 }),
                        ( 0x27B182, new byte[] { 0xFF, 0xC7, 0x48, 0x89, 0x5C, 0x24, 0x70 }, new byte[] { 0xE8, 0xDA, 0x13, 0x00, 0x00, 0x90, 0x90 }),
                        ( 0x27B855, new byte[] { 0x44, 0x8B, 0x7C, 0xC8, 0x08 }, new byte[] { 0xE8, 0x78, 0xD3, 0xFF, 0xFF }),
                        ( 0x27B8B3, new byte[] { 0x0F, 0x1F }, new byte[] { 0xEB, 0x0B }),
                        ( 0x27B8B5, new byte[] { 0x40, 0x00 }, new byte[] { 0xEB, 0xB9 }),
                        ( 0x27B925, new byte[] { 0x3B, 0x74, 0x24, 0x48, 0x0F, 0x82, 0x41, 0xFF, 0xFF, 0xFF }, new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x70, 0x3B, 0x70, 0x08, 0x72, 0x86 }),
                        ( 0x27C561, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC7, 0x3B, 0x7E, 0x08, 0x0F, 0x42, 0x7E, 0x08, 0x48, 0x89, 0x5C, 0x24, 0x78, 0xC3 }),
                        ( 0x27C791, new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, new byte[] { 0xFF, 0xC7, 0x41, 0x3B, 0x79, 0x08, 0x41, 0x0F, 0x42, 0x79, 0x08, 0x83, 0xC0, 0x3C, 0xC3 })
                    }
                )
            },
        };

        // Patch for fixing Reach lightmap color crash
        // For Reach tool.exe/tool_fast.exe version 18/07/2023
        private static readonly Dictionary<ToolType, (string original, string patched, (long offset, byte[] RevertBytes)[] locations)> reachLightmapColorPatchData = new()
        {
            {
                ToolType.Tool,
                (
                    "95CF79AC2308FE54D8003316E71223C3B789228E2EAB453B2C09CE03813B8247", // original
                    "04C44C75D6456E396200B0F048299DF478509ABB6089047D5783523D06B344E4", // patched
                    new (long, byte[])[]
                    {
                        ( 0x17095F, new byte[] { 0xE8, 0x5C, 0x87, 0x68, 0x00 }),
                        ( 0x170975, new byte[] { 0xE8, 0x46, 0x87, 0x68, 0x00 }),
                        ( 0x170990, new byte[] { 0xE8, 0x2B, 0x87, 0x68, 0x00 }),
                        ( 0x171588, new byte[] { 0xE8, 0x33, 0x7B, 0x68, 0x00 }),
                        ( 0x17159E, new byte[] { 0xE8, 0x1D, 0x7B, 0x68, 0x00 }),
                        ( 0x1715B9, new byte[] { 0xE8, 0x02, 0x7B, 0x68, 0x00 })
                    }
                )
            },
            {
                ToolType.ToolFast,
                (
                    "C59F3287BF1ED5C7FFF7306FFD583F98CBF4F3A85D12E13313F565A24566DD47", // original
                    "FE232C0F1EA8CF9AB5380F3DCBF20781D5E68C41D22AA10A21B043EDAE18551A", // patched
                    new (long, byte[])[]
                    {
                        ( 0xF2A02, new byte[] { 0xE8, 0x4D, 0x54, 0x29, 0x00 }),
                        ( 0xF2A18, new byte[] { 0xE8, 0x37, 0x54, 0x29, 0x00 }),
                        ( 0xF2A33, new byte[] { 0xE8, 0x1C, 0x54, 0x29, 0x00 }),
                        ( 0xF2A88, new byte[] { 0xE8, 0xC7, 0x53, 0x29, 0x00 }),
                        ( 0xF2A9E, new byte[] { 0xE8, 0xB1, 0x53, 0x29, 0x00 }),
                        ( 0xF2AB9, new byte[] { 0xE8, 0x96, 0x53, 0x29, 0x00 })
                    }
                )
            }
        };
    }
}
