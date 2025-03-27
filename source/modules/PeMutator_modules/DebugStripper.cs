﻿/*
 * This file is part of the Astral-PE project.
 * Copyright (c) 2025 DosX. All rights reserved.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the “Software”), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 *
 * Astral-PE is a low-level post-compilation PE header mutator (obfuscator) for native
 * Windows x86/x64 binaries. It modifies structural metadata while preserving execution integrity.
 *
 * For source code, updates, and documentation, visit:
 * https://github.com/DosX-dev/Astral-PE
 */

using System;
using System.Linq;
using System.Text;
using PeNet;
using PeNet.Header.Pe;

namespace AstralPE.Obfuscator.Modules {
    public class DebugStripper : IObfuscationModule {

        /// <summary>
        /// Applies the debug cleaning logic to the PE file, including directory cleanup,
        /// export symbol sanitization, and embedded string wiping.
        /// </summary>
        /// <param name="raw">The raw PE file bytes.</param>
        /// <param name="pe">Parsed PE metadata object.</param>
        /// <param name="e_lfanew">Offset to IMAGE_NT_HEADERS.</param>
        /// <param name="optStart">Offset to IMAGE_OPTIONAL_HEADER.</param>
        /// <param name="sectionTableOffset">Offset to section headers.</param>
        /// <param name="rnd">Random number generator (unused).</param>
        public void Apply(ref byte[] raw, PeFile pe, int e_lfanew, int optStart, int sectionTableOffset, Random rnd) {
            if (pe.ImageNtHeaders == null)
                throw new InvalidOperationException();

            // Ensure that the DataDirectory section is within bounds
            if (optStart + 0x60 + 16 * 8 > raw.Length)
                throw new Exception("Optional Header is corrupted or incomplete.");

            // 1. Clear the Debug Directory if present
            if (pe.ImageDebugDirectory != null && pe.ImageDebugDirectory.Any()) {
                foreach (ImageDebugDirectory? dbg in pe.ImageDebugDirectory) {
                    int dbgOffset = (int)dbg.PointerToRawData;
                    int dbgSize = (int)dbg.SizeOfData;

                    // Make sure we clear within bounds of the raw file
                    if (dbgOffset + dbgSize <= raw.Length)
                        Array.Clear(raw, dbgOffset, dbgSize);
                }
            }

            // Clear the Debug entry in DataDirectory (debug pointer in IMAGE_OPTIONAL_HEADER)
            // The Debug entry is located in the DataDirectory at index IMAGE_DIRECTORY_ENTRY_DEBUG
            int debugDirOffset = optStart + 0x60 + ((int)PeNet.Header.Pe.DataDirectoryType.Debug * 8);

            // Ensure we're within bounds and clear the entry (pointer + size)
            if (debugDirOffset + 8 <= raw.Length) {
                Array.Clear(raw, debugDirOffset, 8); // Clear both the pointer and the size of the Debug Directory
            }

            // Now, we explicitly set the DataDirectory Debug entry to zero (in the header)
            ImageDataDirectory? dataDirectoryEntry = pe.ImageNtHeaders.OptionalHeader.DataDirectory[(int)PeNet.Header.Pe.DataDirectoryType.Debug];
            dataDirectoryEntry.VirtualAddress = 0;
            dataDirectoryEntry.Size = 0;

            // Wipe all embedded .pdb paths from binary
            ReadOnlySpan<byte> marker = new byte[] { (byte)'.', (byte)'p', (byte)'d', (byte)'b', 0 };
            Span<byte> span = raw;
            int pos = span.IndexOf(marker);
            while (pos != -1) {
                int start = pos;
                while (start > 0 && span[start - 1] != 0) start--;
                int end = pos + marker.Length;
                while (end < span.Length && span[end] != 0) end++;
                for (int i = start; i < end; i++) span[i] = 0;
                pos = span.Slice(end).IndexOf(marker);
                if (pos != -1) pos += end;
            }

            // Remove DotNetRuntimeDebugHeader if it's an export
            if (pe.ImageNtHeaders == null || pe.ImageSectionHeaders == null)
                throw new InvalidOperationException();

            ImageDataDirectory? exportDir = pe.ImageNtHeaders.OptionalHeader.DataDirectory[0];
            if (exportDir.VirtualAddress == 0 || exportDir.Size == 0)
                return;

            uint expStart = exportDir.VirtualAddress,
                 expEnd = expStart + exportDir.Size;

            byte[] target = Encoding.ASCII.GetBytes("DotNetRuntimeDebugHeader\0");

            int found = Patcher.IndexOf(raw, target);
            if (found != -1) {
                uint rva = Patcher.OffsetToRva((uint)found, pe.ImageSectionHeaders);

                if (rva >= expStart && rva < expEnd) {
                    Patcher.ReplaceBytes(raw, target, new byte[target.Length]);
                }
            }
        }
    }
}
