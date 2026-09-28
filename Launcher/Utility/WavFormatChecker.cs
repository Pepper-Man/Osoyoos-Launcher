using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ToolkitLauncher.Utility
{
    internal class WavFormatChecker
    {
        private readonly record struct AudioData
        (
            short AudioFormat,
            short Channels,
            int SampleRate,
            short Bits
        );

        private static readonly HashSet<AudioData> Gen1ValidFormats =
        [
            new(1, 1, 22050, 16),
            new(1, 1, 44100, 16),
            new(1, 2, 22050, 16),
            new(1, 2, 44100, 16)
        ];

        private static readonly HashSet<AudioData> Gen2ValidFormats =
        [
            new(1, 1, 22050, 16),
            new(1, 1, 32000, 16),
            new(1, 1, 44100, 16),
            new(1, 1, 48000, 16),
            new(1, 2, 22050, 16),
            new(1, 2, 32000, 16),
            new(1, 2, 44100, 16),
            new(1, 2, 48000, 16),
        ];

        private static AudioData? ReadWavHeader(string filePath)
        {
            try
            {
                using var file = File.OpenRead(filePath);
                using BinaryReader reader = new(file);

                reader.ReadBytes(4); // Chunk Id = RIFF
                int riffChunkSize = reader.ReadInt32();
                string waveFormat = Encoding.ASCII.GetString(reader.ReadBytes(4));

                // Don't even bother if we aren't WAVE format
                if (!waveFormat.Equals("WAVE", StringComparison.OrdinalIgnoreCase)) return null;

                // Find the FMT chunk - should be next in most cases, but not guaranteed
                bool foundFmt = false;
                while (file.Position < file.Length - 8)
                {
                    string chunkName = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    int chunkSize = reader.ReadInt32();

                    if (chunkName.Equals("fmt ", StringComparison.OrdinalIgnoreCase))
                    {
                        foundFmt = true;
                        break;
                    }

                    // Skip chunk payload and handle odd byte padding
                    int pad = (chunkSize % 2 != 0) ? 1 : 0;
                    file.Seek(chunkSize + pad, SeekOrigin.Current);
                }

                if (!foundFmt) return null;

                // We are now inside the FMT chunk
                short audioFormat = reader.ReadInt16();     // PCM = 1
                short channels = reader.ReadInt16();        // Mono = 1, Stereo = 2
                int sampleRate = reader.ReadInt32();        // Sample rate in Hz
                reader.ReadInt32();                         // Skip byte rate data
                reader.ReadInt16();                         // Skip block align data
                short bitsPerSample = reader.ReadInt16();   // Bits per sample

                return new AudioData(audioFormat, channels, sampleRate, bitsPerSample);
            }
            catch
            {
                // Catch any unreadable, locked or malformed files
                return null;
            }
        }

        public static List<string> GetInvalidForGen1(string wavFolder)
        {
            List<string> badWavs = [];

            foreach (string filePath in Directory.EnumerateFiles(wavFolder, "*.wav", SearchOption.AllDirectories))
            {
                AudioData? audioData = ReadWavHeader(filePath);

                if (audioData == null || !Gen1ValidFormats.Contains(audioData.Value))
                {
                    badWavs.Add(filePath);
                }
            }

            return badWavs;
        }

        public static List<string> GetInvalidForGen2(string wavFolder)
        {
            List<string> badWavs = [];

            foreach (string filePath in Directory.EnumerateFiles(wavFolder, "*.wav", SearchOption.AllDirectories))
            {
                AudioData? audioData = ReadWavHeader(filePath);

                if (audioData == null || !Gen2ValidFormats.Contains(audioData.Value))
                {
                    badWavs.Add(filePath);
                }
            }

            return badWavs;
        }

        public static List<string> GetInvalidForGen3(string wavFolder)
        {
            List<string> badWavs = [];

            foreach (string filePath in Directory.EnumerateFiles(wavFolder, "*.wav", SearchOption.AllDirectories))
            {
                AudioData? audioData = ReadWavHeader(filePath);

                // Must be read, PCM, and 16 or 32 bit. We ignore sample rate since it doesn't seem to care - tested 0.5KHz through 384KHz
                if (audioData == null || audioData.Value.AudioFormat != 1 || (audioData.Value.Bits != 16 && audioData.Value.Bits != 32))
                {
                    badWavs.Add(filePath);
                }
            }

            return badWavs;
        }
    }
}
