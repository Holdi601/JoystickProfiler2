// JoyPro.WtOodle: Oodle LZ (Kraken / Leviathan) decompression for War Thunder's packed cockpit data.
//
// This file is a C# port of parts of "ooz" (Kraken Decompressor for Windows, Copyright (C) 2016 Powzix,
// https://github.com/powzix/ooz), which is licensed under the GNU General Public License version 3 or later.
// The port is licensed the same way (GPL-3.0-or-later, see tools/wt-oodle/README.md). It is a separate program:
// the engine starts it as its own process and never links it.
//
// Changes against ooz: managed buffers with guard space instead of raw caller memory, a scratch area sized for the
// data (ooz' fixed 0x6C000 scratch rejects some valid Leviathan chunks), a Huffman block with a single symbol
// returns its size (ooz returned a negative count), multi-array streams may hold up to 63 entropy arrays (ooz:
// 32, which overran on some War Thunder data), trailing bytes after the last quantum are allowed, and the
// caller may stop after the first N output bytes (whole 256 KB blocks).
#nullable disable
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace JoyPro.WtOodle
{
    public static unsafe class OodleLz
    {
        private const int Guard = 64;

        // Decompresses src[srcOffset .. srcOffset + srcLength) into rawLength bytes. With stopAfter > 0 the decoder
        // stops once at least that many bytes are written (the rest of the result is zero).
        public static byte[] Decompress(byte[] src, int srcOffset, int srcLength, int rawLength, int stopAfter = 0)
        {
            if (src == null || srcOffset < 0 || srcLength < 0 || srcOffset + srcLength > src.Length || rawLength < 0)
                throw new ArgumentException("bad range");
            byte[] input = new byte[srcLength + 2 * Guard];
            Buffer.BlockCopy(src, srcOffset, input, Guard, srcLength);
            byte[] output = new byte[rawLength + Guard];
            int scratchSize = 0x6C000 * 4;
            byte[] scratch = new byte[scratchSize + Guard];
            fixed (byte* pin = input)
            fixed (byte* pout = output)
            fixed (byte* pscr = scratch)
            {
                Decoder dec = new Decoder { Scratch = pscr, ScratchSize = scratchSize };
                byte* s = pin + Guard;
                long sLeft = srcLength;
                int offset = 0;
                int left = rawLength;
                while (left != 0)
                {
                    if (!dec.DecodeStep(pout, offset, left, s, sLeft))
                        throw new InvalidOperationException("Oodle data is corrupt at output offset " + offset + " (" + dec.Error + ")");
                    if (dec.SrcUsed == 0)
                        throw new InvalidOperationException("Oodle data ends early at output offset " + offset);
                    s += dec.SrcUsed;
                    sLeft -= dec.SrcUsed;
                    left -= dec.DstUsed;
                    offset += dec.DstUsed;
                    if (stopAfter > 0 && offset >= stopAfter)
                        break;
                }
            }
            if (output.Length != rawLength)
                Array.Resize(ref output, rawLength);
            return output;
        }

        private struct Header
        {
            public int DecoderType;
            public bool Restart, Uncompressed, Checksums;
        }

        private struct QuantumHeader
        {
            public uint CompressedSize, Checksum, WholeMatchDistance;
            public byte Flag1, Flag2;
        }

        private struct BitReader
        {
            public byte* P, PEnd;
            public uint Bits;
            public int BitPos;

            public void Refill()
            {
                while (BitPos > 0)
                {
                    Bits |= (uint)(P < PEnd ? *P : 0) << BitPos;
                    BitPos -= 8;
                    P++;
                }
            }

            public void RefillBackwards()
            {
                while (BitPos > 0)
                {
                    P--;
                    Bits |= (uint)(P >= PEnd ? *P : 0) << BitPos;
                    BitPos -= 8;
                }
            }

            public int ReadBit()
            {
                Refill();
                return ReadBitNoRefill();
            }

            public int ReadBitNoRefill()
            {
                int r = (int)(Bits >> 31);
                Bits <<= 1;
                BitPos += 1;
                return r;
            }

            public int ReadBitsNoRefill(int n)
            {
                int r = (int)(Bits >> (32 - n));
                Bits <<= n;
                BitPos += n;
                return r;
            }

            public int ReadBitsNoRefillZero(int n)
            {
                int r = (int)(Bits >> 1 >> (31 - n));
                Bits <<= n;
                BitPos += n;
                return r;
            }

            public uint ReadMoreThan24Bits(int n)
            {
                uint rv;
                if (n <= 24)
                {
                    rv = (uint)ReadBitsNoRefillZero(n);
                }
                else
                {
                    rv = (uint)ReadBitsNoRefill(24) << (n - 24);
                    Refill();
                    rv += (uint)ReadBitsNoRefill(n - 24);
                }
                Refill();
                return rv;
            }

            public uint ReadMoreThan24BitsB(int n)
            {
                uint rv;
                if (n <= 24)
                {
                    rv = (uint)ReadBitsNoRefillZero(n);
                }
                else
                {
                    rv = (uint)ReadBitsNoRefill(24) << (n - 24);
                    RefillBackwards();
                    rv += (uint)ReadBitsNoRefill(n - 24);
                }
                RefillBackwards();
                return rv;
            }

            public uint ReadDistance(uint v, bool backwards)
            {
                uint w, m, n, rv;
                if (v < 0xF0)
                {
                    n = (v >> 4) + 4;
                    w = BitOperations.RotateLeft(Bits | 1, (int)n);
                    BitPos += (int)n;
                    m = (2u << (int)n) - 1;
                    Bits = w & ~m;
                    rv = ((w & m) << 4) + (v & 0xF) - 248;
                }
                else
                {
                    n = v - 0xF0 + 4;
                    w = BitOperations.RotateLeft(Bits | 1, (int)n);
                    BitPos += (int)n;
                    m = (2u << (int)n) - 1;
                    Bits = w & ~m;
                    rv = 8322816 + ((w & m) << 12);
                    if (backwards)
                        RefillBackwards();
                    else
                        Refill();
                    rv += Bits >> 20;
                    BitPos += 12;
                    Bits <<= 12;
                }
                if (backwards)
                    RefillBackwards();
                else
                    Refill();
                return rv;
            }

            public bool ReadLength(out uint v, bool backwards)
            {
                v = 0;
                if (Bits == 0)
                    return false;
                int n = BitOperations.LeadingZeroCount(Bits);
                if (n > 12)
                    return false;
                BitPos += n;
                Bits <<= n;
                if (backwards)
                    RefillBackwards();
                else
                    Refill();
                n += 7;
                BitPos += n;
                v = (Bits >> (32 - n)) - 64;
                Bits <<= n;
                if (backwards)
                    RefillBackwards();
                else
                    Refill();
                return true;
            }
        }

        private struct BitReader2
        {
            public byte* P, PEnd;
            public uint BitPos;
        }

        private static int Clz(uint x)
        {
            return BitOperations.LeadingZeroCount(x);
        }

        private static int Bsr(uint x)
        {
            return 31 - BitOperations.LeadingZeroCount(x);
        }

        private static uint ReadU32(byte* p) { return *(uint*)p; }
        private static ushort ReadU16(byte* p) { return *(ushort*)p; }
        private static uint Swap(uint v) { return BinaryPrimitives.ReverseEndianness(v); }

        private static void Copy64(byte* d, byte* s)
        {
            *(ulong*)d = *(ulong*)s;
        }

        private static void Copy64Add(byte* d, byte* s, byte* t)
        {
            ulong a = *(ulong*)s, b = *(ulong*)t;
            *(ulong*)d = ((a & 0x7F7F7F7F7F7F7F7FUL) + (b & 0x7F7F7F7F7F7F7F7FUL)) ^ ((a ^ b) & 0x8080808080808080UL);
        }

        private static byte* Align(byte* p, int align)
        {
            return (byte*)(((long)p + (align - 1)) & ~(long)(align - 1));
        }

        private sealed class Decoder
        {
            public byte* Scratch;
            public int ScratchSize;
            public Header Hdr;
            public int SrcUsed, DstUsed;
            public string Error = string.Empty;

            public bool DecodeStep(byte* dstStart, int offset, int dstBytesLeftIn, byte* src, long srcBytesLeft)
            {
                byte* srcIn = src;
                byte* srcEnd = src + srcBytesLeft;
                if ((offset & 0x3FFFF) == 0)
                {
                    src = ParseHeader(ref Hdr, src);
                    if (src == null)
                    {
                        Error = "block header";
                        return false;
                    }
                }
                bool krakenFamily = Hdr.DecoderType == 6 || Hdr.DecoderType == 10 || Hdr.DecoderType == 12;
                if (!krakenFamily)
                {
                    Error = "decoder type " + Hdr.DecoderType + " is not supported";
                    return false;
                }
                int dstBytesLeft = Math.Min(0x40000, dstBytesLeftIn);
                if (Hdr.Uncompressed)
                {
                    if (srcEnd - src < dstBytesLeft)
                    {
                        SrcUsed = DstUsed = 0;
                        return true;
                    }
                    Buffer.MemoryCopy(src, dstStart + offset, dstBytesLeft, dstBytesLeft);
                    SrcUsed = (int)(src - srcIn) + dstBytesLeft;
                    DstUsed = dstBytesLeft;
                    return true;
                }
                QuantumHeader q = default(QuantumHeader);
                src = ParseQuantumHeader(ref q, src, Hdr.Checksums);
                if (src == null || src > srcEnd)
                {
                    Error = "quantum header";
                    return false;
                }
                if ((ulong)(srcEnd - src) < q.CompressedSize)
                {
                    SrcUsed = DstUsed = 0;
                    return true;
                }
                if (q.CompressedSize > (uint)dstBytesLeft)
                {
                    Error = "quantum larger than its output";
                    return false;
                }
                if (q.CompressedSize == 0)
                {
                    if (q.WholeMatchDistance != 0)
                    {
                        Error = "whole match";
                        return false;
                    }
                    new Span<byte>(dstStart + offset, dstBytesLeft).Fill((byte)q.Checksum);
                    SrcUsed = (int)(src - srcIn);
                    DstUsed = dstBytesLeft;
                    return true;
                }
                if (q.CompressedSize == dstBytesLeft)
                {
                    Buffer.MemoryCopy(src, dstStart + offset, dstBytesLeft, dstBytesLeft);
                    SrcUsed = (int)(src - srcIn) + dstBytesLeft;
                    DstUsed = dstBytesLeft;
                    return true;
                }
                int n;
                if (Hdr.DecoderType == 6)
                    n = KrakenDecodeQuantum(dstStart + offset, dstStart + offset + dstBytesLeft, dstStart, src, src + q.CompressedSize);
                else if (Hdr.DecoderType == 12)
                    n = LeviathanDecodeQuantum(dstStart + offset, dstStart + offset + dstBytesLeft, dstStart, src, src + q.CompressedSize);
                else
                {
                    Error = "Mermaid is not supported";
                    return false;
                }
                if (n != q.CompressedSize)
                {
                    if (Error.Length == 0)
                        Error = "quantum decode";
                    return false;
                }
                SrcUsed = (int)(src - srcIn) + n;
                DstUsed = dstBytesLeft;
                return true;
            }

            private int KrakenDecodeQuantum(byte* dst, byte* dstEnd, byte* dstStart, byte* src, byte* srcEnd)
            {
                byte* srcIn = src;
                while (dstEnd - dst != 0)
                {
                    int dstCount = (int)Math.Min(dstEnd - dst, 0x20000);
                    if (srcEnd - src < 4)
                        return -1;
                    int chunkhdr = src[2] | src[1] << 8 | src[0] << 16;
                    int srcUsed;
                    if ((chunkhdr & 0x800000) == 0)
                    {
                        byte* o = dst;
                        int written;
                        srcUsed = DecodeBytes(&o, src, srcEnd, &written, dstCount, false, Scratch, Scratch + ScratchSize);
                        if (srcUsed < 0 || written != dstCount)
                            return -1;
                    }
                    else
                    {
                        src += 3;
                        srcUsed = chunkhdr & 0x7FFFF;
                        int mode = (chunkhdr >> 19) & 0xF;
                        if (srcEnd - src < srcUsed)
                            return -1;
                        if (srcUsed < dstCount)
                        {
                            KrakenLzTable lzt = new KrakenLzTable();
                            if (!KrakenReadLzTable(mode, src, src + srcUsed, dst, dstCount, (int)(dst - dstStart), Scratch, Scratch + ScratchSize, lzt))
                            {
                                Error = "Kraken tables";
                                return -1;
                            }
                            if (!KrakenProcessLzRuns(mode, dst, dstCount, (int)(dst - dstStart), lzt))
                            {
                                Error = "Kraken matches";
                                return -1;
                            }
                        }
                        else if (srcUsed > dstCount || mode != 0)
                        {
                            return -1;
                        }
                        else
                        {
                            Buffer.MemoryCopy(src, dst, dstCount, dstCount);
                        }
                    }
                    src += srcUsed;
                    dst += dstCount;
                }
                return (int)(src - srcIn);
            }

            private int LeviathanDecodeQuantum(byte* dst, byte* dstEnd, byte* dstStart, byte* src, byte* srcEnd)
            {
                byte* srcIn = src;
                while (dstEnd - dst != 0)
                {
                    int dstCount = (int)Math.Min(dstEnd - dst, 0x20000);
                    if (srcEnd - src < 4)
                        return -1;
                    int chunkhdr = src[2] | src[1] << 8 | src[0] << 16;
                    int srcUsed;
                    if ((chunkhdr & 0x800000) == 0)
                    {
                        byte* o = dst;
                        int written;
                        srcUsed = DecodeBytes(&o, src, srcEnd, &written, dstCount, false, Scratch, Scratch + ScratchSize);
                        if (srcUsed < 0 || written != dstCount)
                        {
                            Error = "entropy-only chunk";
                            return -1;
                        }
                    }
                    else
                    {
                        src += 3;
                        srcUsed = chunkhdr & 0x7FFFF;
                        int mode = (chunkhdr >> 19) & 0xF;
                        if (srcEnd - src < srcUsed)
                            return -1;
                        if (srcUsed < dstCount)
                        {
                            LeviathanLzTable lzt = new LeviathanLzTable();
                            if (!LeviathanReadLzTable(mode, src, src + srcUsed, dst, dstCount, (int)(dst - dstStart), Scratch, Scratch + ScratchSize, lzt))
                            {
                                Error = "Leviathan tables (mode " + mode + ")";
                                return -1;
                            }
                            if (!LeviathanProcessLzRuns(mode, dst, dstCount, (int)(dst - dstStart), lzt))
                            {
                                Error = "Leviathan matches (mode " + mode + ")";
                                return -1;
                            }
                        }
                        else if (srcUsed > dstCount || mode != 0)
                        {
                            return -1;
                        }
                        else
                        {
                            Buffer.MemoryCopy(src, dst, dstCount, dstCount);
                        }
                    }
                    src += srcUsed;
                    dst += dstCount;
                }
                return (int)(src - srcIn);
            }
        }

        private static byte* ParseHeader(ref Header hdr, byte* p)
        {
            int b = p[0];
            if ((b & 0xF) != 0xC)
                return null;
            if (((b >> 4) & 3) != 0)
                return null;
            hdr.Restart = ((b >> 7) & 1) != 0;
            hdr.Uncompressed = ((b >> 6) & 1) != 0;
            b = p[1];
            hdr.DecoderType = b & 0x7F;
            hdr.Checksums = (b >> 7) != 0;
            if (hdr.DecoderType != 6 && hdr.DecoderType != 10 && hdr.DecoderType != 5 && hdr.DecoderType != 11 && hdr.DecoderType != 12)
                return null;
            return p + 2;
        }

        private static byte* ParseQuantumHeader(ref QuantumHeader hdr, byte* p, bool checksum)
        {
            uint v = (uint)(p[0] << 16 | p[1] << 8 | p[2]);
            uint size = v & 0x3FFFF;
            if (size != 0x3FFFF)
            {
                hdr.CompressedSize = size + 1;
                hdr.Flag1 = (byte)((v >> 18) & 1);
                hdr.Flag2 = (byte)((v >> 19) & 1);
                if (checksum)
                {
                    hdr.Checksum = (uint)(p[3] << 16 | p[4] << 8 | p[5]);
                    return p + 6;
                }
                return p + 3;
            }
            v >>= 18;
            if (v == 1)
            {
                hdr.Checksum = p[3];
                hdr.CompressedSize = 0;
                hdr.WholeMatchDistance = 0;
                return p + 4;
            }
            return null;
        }

        // ---- Entropy layer ----------------------------------------------------------------------------------------

        private struct HuffRange
        {
            public int Symbol, Num;
        }

        private static readonly byte[] RevIndex = BuildRevIndex();

        private static byte[] BuildRevIndex()
        {
            // not used as table of bytes; see ReverseLut
            return new byte[0];
        }

        private static int Reverse11(int i)
        {
            int r = 0;
            for (int b = 0; b < 11; b++)
                r |= ((i >> b) & 1) << (10 - b);
            return r;
        }

        private static readonly int[] Rev11 = BuildRev11();

        private static int[] BuildRev11()
        {
            int[] r = new int[2048];
            for (int i = 0; i < 2048; i++)
                r[i] = Reverse11(i);
            return r;
        }

        private static bool DecodeBytesCore(byte* output, byte* outputEnd, byte* src, byte* srcMid, byte* srcEnd, byte* len, byte* sym)
        {
            byte* srcMidOrg = srcMid;
            uint srcBits = 0, srcMidBits = 0, srcEndBits = 0;
            int srcBitpos = 0, srcMidBitpos = 0, srcEndBitpos = 0;
            int k, n;
            byte* dst = output;
            byte* dstEnd = outputEnd;
            if (src > srcMid)
                return false;
            if (srcEnd - srcMid >= 4 && dstEnd - dst >= 6)
            {
                dstEnd -= 5;
                srcEnd -= 4;
                while (dst < dstEnd && src <= srcMid && srcMid <= srcEnd)
                {
                    srcBits |= ReadU32(src) << srcBitpos;
                    src += (31 - srcBitpos) >> 3;
                    srcEndBits |= Swap(ReadU32(srcEnd)) << srcEndBitpos;
                    srcEnd -= (31 - srcEndBitpos) >> 3;
                    srcMidBits |= ReadU32(srcMid) << srcMidBitpos;
                    srcMid += (31 - srcMidBitpos) >> 3;
                    srcBitpos |= 0x18;
                    srcEndBitpos |= 0x18;
                    srcMidBitpos |= 0x18;

                    k = (int)(srcBits & 0x7FF); n = len[k]; srcBits >>= n; srcBitpos -= n; dst[0] = sym[k];
                    k = (int)(srcEndBits & 0x7FF); n = len[k]; srcEndBits >>= n; srcEndBitpos -= n; dst[1] = sym[k];
                    k = (int)(srcMidBits & 0x7FF); n = len[k]; srcMidBits >>= n; srcMidBitpos -= n; dst[2] = sym[k];
                    k = (int)(srcBits & 0x7FF); n = len[k]; srcBits >>= n; srcBitpos -= n; dst[3] = sym[k];
                    k = (int)(srcEndBits & 0x7FF); n = len[k]; srcEndBits >>= n; srcEndBitpos -= n; dst[4] = sym[k];
                    k = (int)(srcMidBits & 0x7FF); n = len[k]; srcMidBits >>= n; srcMidBitpos -= n; dst[5] = sym[k];
                    dst += 6;
                }
                dstEnd += 5;
                src -= srcBitpos >> 3;
                srcBitpos &= 7;
                srcEnd += 4 + (srcEndBitpos >> 3);
                srcEndBitpos &= 7;
                srcMid -= srcMidBitpos >> 3;
                srcMidBitpos &= 7;
            }
            for (;;)
            {
                if (dst >= dstEnd)
                    break;
                if (srcMid - src <= 1)
                {
                    if (srcMid - src == 1)
                        srcBits |= (uint)*src << srcBitpos;
                }
                else
                {
                    srcBits |= (uint)ReadU16(src) << srcBitpos;
                }
                k = (int)(srcBits & 0x7FF);
                n = len[k];
                srcBitpos -= n;
                srcBits >>= n;
                *dst++ = sym[k];
                src += (7 - srcBitpos) >> 3;
                srcBitpos &= 7;
                if (dst < dstEnd)
                {
                    if (srcEnd - srcMid <= 1)
                    {
                        if (srcEnd - srcMid == 1)
                        {
                            srcEndBits |= (uint)*srcMid << srcEndBitpos;
                            srcMidBits |= (uint)*srcMid << srcMidBitpos;
                        }
                    }
                    else
                    {
                        uint v = ReadU16(srcEnd - 2);
                        srcEndBits |= (((v >> 8) | (v << 8)) & 0xFFFF) << srcEndBitpos;
                        srcMidBits |= (uint)ReadU16(srcMid) << srcMidBitpos;
                    }
                    n = len[srcEndBits & 0x7FF];
                    *dst++ = sym[srcEndBits & 0x7FF];
                    srcEndBitpos -= n;
                    srcEndBits >>= n;
                    srcEnd -= (7 - srcEndBitpos) >> 3;
                    srcEndBitpos &= 7;
                    if (dst < dstEnd)
                    {
                        n = len[srcMidBits & 0x7FF];
                        *dst++ = sym[srcMidBits & 0x7FF];
                        srcMidBitpos -= n;
                        srcMidBits >>= n;
                        srcMid += (7 - srcMidBitpos) >> 3;
                        srcMidBitpos &= 7;
                    }
                }
                if (src > srcMid || srcMid > srcEnd)
                    return false;
            }
            if (src != srcMidOrg || srcEnd != srcMid)
                return false;
            return true;
        }

        private static int ReadCodeLengthsOld(ref BitReader bits, byte* syms, uint* codePrefix)
        {
            if (bits.ReadBitNoRefill() != 0)
            {
                int n, sym = 0, codelen, numSymbols = 0;
                int avgBitsX4 = 32;
                int forcedBits = bits.ReadBitsNoRefill(2);
                uint thres = 1u << (31 - (int)(20u >> forcedBits));
                bool skipZeros = bits.ReadBit() != 0;
                do
                {
                    if (!skipZeros)
                    {
                        if ((bits.Bits & 0xFF000000) == 0)
                            return -1;
                        sym += bits.ReadBitsNoRefill(2 * (Clz(bits.Bits) + 1)) - 2 + 1;
                        if (sym >= 256)
                            break;
                    }
                    skipZeros = false;
                    bits.Refill();
                    if ((bits.Bits & 0xFF000000) == 0)
                        return -1;
                    n = bits.ReadBitsNoRefill(2 * (Clz(bits.Bits) + 1)) - 2 + 1;
                    if (sym + n > 256)
                        return -1;
                    bits.Refill();
                    numSymbols += n;
                    do
                    {
                        if (bits.Bits < thres)
                            return -1;
                        int lz = Clz(bits.Bits);
                        int v = bits.ReadBitsNoRefill(lz + forcedBits + 1) + ((lz - 1) << forcedBits);
                        codelen = (-(v & 1) ^ (v >> 1)) + ((avgBitsX4 + 2) >> 2);
                        if (codelen < 1 || codelen > 11)
                            return -1;
                        avgBitsX4 = codelen + ((3 * avgBitsX4 + 2) >> 2);
                        bits.Refill();
                        syms[codePrefix[codelen]++] = (byte)sym++;
                    } while (--n != 0);
                } while (sym != 256);
                return sym == 256 && numSymbols >= 2 ? numSymbols : -1;
            }
            else
            {
                int numSymbols = bits.ReadBitsNoRefill(8);
                if (numSymbols == 0)
                    return -1;
                if (numSymbols == 1)
                {
                    syms[0] = (byte)bits.ReadBitsNoRefill(8);
                }
                else
                {
                    int codelenBits = bits.ReadBitsNoRefill(3);
                    if (codelenBits > 4)
                        return -1;
                    for (int i = 0; i < numSymbols; i++)
                    {
                        bits.Refill();
                        int sym = bits.ReadBitsNoRefill(8);
                        int codelen = bits.ReadBitsNoRefillZero(codelenBits) + 1;
                        if (codelen > 11)
                            return -1;
                        syms[codePrefix[codelen]++] = (byte)sym;
                    }
                }
                return numSymbols;
            }
        }

        private static int ReadFluff(ref BitReader bits, int numSymbols)
        {
            if (numSymbols == 256)
                return 0;
            int x = 257 - numSymbols;
            if (x > numSymbols)
                x = numSymbols;
            x *= 2;
            int y = Bsr((uint)(x - 1)) + 1;
            uint v = bits.Bits >> (32 - y);
            uint z = (1u << y) - (uint)x;
            if ((v >> 1) >= z)
            {
                bits.Bits <<= y;
                bits.BitPos += y;
                return (int)(v - z);
            }
            bits.Bits <<= y - 1;
            bits.BitPos += y - 1;
            return (int)(v >> 1);
        }

        private static readonly uint[] RiceValue = BuildRiceValue();
        private static readonly byte[] RiceLen = BuildRiceLen();

        // For each byte: the unary values (zeros before each set bit, MSB first) packed into nibbles of two uint
        // halves, and the count of leading zeros carried after the last set bit (top nibble). Built instead of the
        // literal table; same content.
        private static uint[] BuildRiceValue()
        {
            uint[] t = new uint[256];
            for (int v = 0; v < 256; v++)
            {
                if (v == 0)
                {
                    t[v] = 0x80000000;
                    continue;
                }
                int[] vals = new int[8];
                int count = 0, zeros = 0;
                for (int b = 7; b >= 0; b--)
                {
                    if (((v >> b) & 1) != 0)
                    {
                        vals[count++] = zeros;
                        zeros = 0;
                    }
                    else
                    {
                        zeros++;
                    }
                }
                // nibble layout: dst[0..3] = low nibbles of bytes 0..3, dst[4..7] = high nibbles
                uint x = 0;
                for (int i = 0; i < count && i < 8; i++)
                {
                    int byteIndex = i & 3;
                    int shift = byteIndex * 8 + (i >= 4 ? 4 : 0);
                    x |= (uint)vals[i] << shift;
                }
                x |= (uint)zeros << 28;
                t[v] = x;
            }
            return t;
        }

        private static byte[] BuildRiceLen()
        {
            byte[] t = new byte[256];
            for (int v = 0; v < 256; v++)
                t[v] = (byte)BitOperations.PopCount((uint)v);
            return t;
        }

        private static bool DecodeGolombRiceLengths(byte* dst, int size, ref BitReader2 br)
        {
            byte* p = br.P, pEnd = br.PEnd;
            byte* dstEnd = dst + size;
            if (p >= pEnd)
                return false;
            int count = -(int)br.BitPos;
            uint v = (uint)(*p++ & (255 >> (int)br.BitPos));
            for (;;)
            {
                if (v == 0)
                {
                    count += 8;
                }
                else
                {
                    uint x = RiceValue[v];
                    *(uint*)&dst[0] = (uint)(count + (int)(x & 0x0F0F0F0F));
                    *(uint*)&dst[4] = (x >> 4) & 0x0F0F0F0F;
                    dst += RiceLen[v];
                    if (dst >= dstEnd)
                        break;
                    count = (int)(x >> 28);
                }
                if (p >= pEnd)
                    return false;
                v = *p++;
            }
            if (dst > dstEnd)
            {
                int n = (int)(dst - dstEnd);
                do
                    v &= v - 1;
                while (--n != 0);
            }
            uint bitpos = 0;
            if ((v & 1) == 0)
            {
                p--;
                int q = BitOperations.TrailingZeroCount(v);
                bitpos = (uint)(8 - q);
            }
            br.P = p;
            br.BitPos = bitpos;
            return true;
        }

        private static bool DecodeGolombRiceBits(byte* dst, uint size, uint bitcount, ref BitReader2 br)
        {
            if (bitcount == 0)
                return true;
            byte* p = br.P;
            int bitpos = (int)br.BitPos;
            uint bitsRequired = (uint)bitpos + bitcount * size;
            uint bytesRequired = (bitsRequired + 7) >> 3;
            if (bytesRequired > br.PEnd - p)
                return false;
            br.P = p + (bitsRequired >> 3);
            br.BitPos = bitsRequired & 7;
            // Plain loop: each value gets bitcount more low bits, read MSB first from the bit stream.
            long bitIndex = bitpos;
            for (uint i = 0; i < size; i++)
            {
                uint extra = 0;
                for (uint b = 0; b < bitcount; b++)
                {
                    int byteVal = p[bitIndex >> 3];
                    int bit = (byteVal >> (7 - (int)(bitIndex & 7))) & 1;
                    extra = (extra << 1) | (uint)bit;
                    bitIndex++;
                }
                dst[i] = (byte)((dst[i] << (int)bitcount) + extra);
            }
            return true;
        }

        private static int ConvertToRanges(HuffRange* range, int numSymbols, int pval, byte* symlen, ref BitReader bits)
        {
            int numRanges = pval >> 1, v, symIdx = 0;
            if ((pval & 1) != 0)
            {
                bits.Refill();
                v = *symlen++;
                if (v >= 8)
                    return -1;
                symIdx = bits.ReadBitsNoRefill(v + 1) + (1 << (v + 1)) - 1;
            }
            int symsUsed = 0;
            for (int i = 0; i < numRanges; i++)
            {
                bits.Refill();
                v = symlen[0];
                if (v >= 9)
                    return -1;
                int num = bits.ReadBitsNoRefillZero(v) + (1 << v);
                v = symlen[1];
                if (v >= 8)
                    return -1;
                int space = bits.ReadBitsNoRefill(v + 1) + (1 << (v + 1)) - 1;
                range[i].Symbol = symIdx;
                range[i].Num = num;
                symsUsed += num;
                symIdx += num + space;
                symlen += 2;
            }
            if (symIdx >= 256 || symsUsed >= numSymbols || symIdx + numSymbols - symsUsed > 256)
                return -1;
            range[numRanges].Symbol = symIdx;
            range[numRanges].Num = numSymbols - symsUsed;
            return numRanges + 1;
        }

        private static int ReadCodeLengthsNew(ref BitReader bits, byte* syms, uint* codePrefix)
        {
            int forcedBits = bits.ReadBitsNoRefill(2);
            int numSymbols = bits.ReadBitsNoRefill(8) + 1;
            int fluff = ReadFluff(ref bits, numSymbols);
            byte* codeLen = stackalloc byte[512 + 32];
            new Span<byte>(codeLen, 512 + 32).Clear();
            BitReader2 br2;
            br2.BitPos = (uint)((bits.BitPos - 24) & 7);
            br2.PEnd = bits.PEnd;
            br2.P = bits.P - (uint)((24 - bits.BitPos + 7) >> 3);
            if (!DecodeGolombRiceLengths(codeLen, numSymbols + fluff, ref br2))
                return -1;
            new Span<byte>(codeLen + numSymbols + fluff, 16).Clear();
            if (!DecodeGolombRiceBits(codeLen, (uint)numSymbols, (uint)forcedBits, ref br2))
                return -1;
            bits.BitPos = 24;
            bits.P = br2.P;
            bits.Bits = 0;
            bits.Refill();
            bits.Bits <<= (int)br2.BitPos;
            bits.BitPos += (int)br2.BitPos;
            uint runningSum = 0x1E;
            for (int i = 0; i < numSymbols; i++)
            {
                int v = codeLen[i];
                v = -(v & 1) ^ (v >> 1);
                int cl = v + (int)(runningSum >> 2) + 1;
                if (cl < 1 || cl > 11)
                    return -1;
                codeLen[i] = (byte)cl;
                runningSum = (uint)((int)runningSum + v);
            }
            HuffRange* range = stackalloc HuffRange[128];
            int ranges = ConvertToRanges(range, numSymbols, fluff, &codeLen[numSymbols], ref bits);
            if (ranges <= 0)
                return -1;
            byte* cp = codeLen;
            for (int i = 0; i < ranges; i++)
            {
                int sym = range[i].Symbol;
                int n = range[i].Num;
                do
                {
                    syms[codePrefix[*cp++]++] = (byte)sym++;
                } while (--n != 0);
            }
            return numSymbols;
        }

        private static bool MakeLut(uint* prefixOrg, uint* prefixCur, byte* bits2len, byte* bits2sym, byte* syms)
        {
            uint currslot = 0;
            for (uint i = 1; i < 11; i++)
            {
                uint start = prefixOrg[i];
                uint count = prefixCur[i] - start;
                if (count != 0)
                {
                    uint stepsize = 1u << (int)(11 - i);
                    uint numToSet = count << (int)(11 - i);
                    if (currslot + numToSet > 2048)
                        return false;
                    new Span<byte>(&bits2len[currslot], (int)numToSet).Fill((byte)i);
                    byte* p = &bits2sym[currslot];
                    for (uint j = 0; j != count; j++, p += stepsize)
                        new Span<byte>(p, (int)stepsize).Fill(syms[start + j]);
                    currslot += numToSet;
                }
            }
            if (prefixCur[11] - prefixOrg[11] != 0)
            {
                uint numToSet = prefixCur[11] - prefixOrg[11];
                if (currslot + numToSet > 2048)
                    return false;
                new Span<byte>(&bits2len[currslot], (int)numToSet).Fill(11);
                Buffer.MemoryCopy(&syms[prefixOrg[11]], &bits2sym[currslot], numToSet, numToSet);
                currslot += numToSet;
            }
            return currslot == 2048;
        }

        private static readonly uint[] CodePrefixOrg = { 0x0, 0x0, 0x2, 0x6, 0xE, 0x1E, 0x3E, 0x7E, 0xFE, 0x1FE, 0x2FE, 0x3FE };

        private static int DecodeBytesType12(byte* src, int srcSize, byte* output, int outputSize, int type)
        {
            byte* srcEnd = src + srcSize;
            BitReader bits = new BitReader { BitPos = 24, Bits = 0, P = src, PEnd = srcEnd };
            bits.Refill();
            uint* prefixOrg = stackalloc uint[12];
            uint* codePrefix = stackalloc uint[12];
            for (int i = 0; i < 12; i++)
                prefixOrg[i] = codePrefix[i] = CodePrefixOrg[i];
            byte* syms = stackalloc byte[1280 + 16];
            int numSyms;
            if (bits.ReadBitNoRefill() == 0)
                numSyms = ReadCodeLengthsOld(ref bits, syms, codePrefix);
            else if (bits.ReadBitNoRefill() == 0)
                numSyms = ReadCodeLengthsNew(ref bits, syms, codePrefix);
            else
                return -1;
            if (numSyms < 1)
                return -1;
            src = bits.P - ((24 - bits.BitPos) / 8);
            if (numSyms == 1)
            {
                new Span<byte>(output, outputSize).Fill(syms[0]);
                return srcSize;
            }
            byte* lutLen = stackalloc byte[2048 + 16];
            byte* lutSym = stackalloc byte[2048 + 16];
            if (!MakeLut(prefixOrg, codePrefix, lutLen, lutSym, syms))
                return -1;
            byte* revLen = stackalloc byte[2048];
            byte* revSym = stackalloc byte[2048];
            for (int i = 0; i < 2048; i++)
            {
                int r = Rev11[i];
                revLen[r] = lutLen[i];
                revSym[r] = lutSym[i];
            }
            if (type == 1)
            {
                if (src + 3 > srcEnd)
                    return -1;
                uint splitMid = ReadU16(src);
                src += 2;
                if (!DecodeBytesCore(output, output + outputSize, src, src + splitMid, srcEnd, revLen, revSym))
                    return -1;
            }
            else
            {
                if (src + 6 > srcEnd)
                    return -1;
                int halfOutputSize = (outputSize + 1) >> 1;
                uint splitMid = ReadU32(src) & 0xFFFFFF;
                src += 3;
                if (splitMid > srcEnd - src)
                    return -1;
                byte* srcMid = src + splitMid;
                uint splitLeft = ReadU16(src);
                src += 2;
                if (srcMid - src < splitLeft + 2 || srcEnd - srcMid < 3)
                    return -1;
                uint splitRight = ReadU16(srcMid);
                if (srcEnd - (srcMid + 2) < splitRight + 2)
                    return -1;
                if (!DecodeBytesCore(output, output + halfOutputSize, src, src + splitLeft, srcMid, revLen, revSym))
                    return -1;
                if (!DecodeBytesCore(output + halfOutputSize, output + outputSize, srcMid + 2, srcMid + 2 + splitRight, srcEnd, revLen, revSym))
                    return -1;
            }
            return srcSize;
        }

        private static readonly uint[] BitMasks = BuildBitMasks();

        private static uint[] BuildBitMasks()
        {
            uint[] m = new uint[32];
            for (int i = 0; i < 32; i++)
                m[i] = i == 31 ? 0xFFFFFFFF : (2u << i) - 1;
            return m;
        }

        private static int DecodeMultiArray(byte* src, byte* srcEnd, byte* dst, byte* dstEnd, byte** arrayData, int* arrayLens, int arrayCount, int* totalSizeOut, bool forceMemmove, byte* scratch, byte* scratchEnd)
        {
            byte* srcOrg = src;
            if (srcEnd - src < 4)
                return -1;
            int decodedSize;
            int numArraysInFile = *src++;
            if ((numArraysInFile & 0x80) == 0)
                return -1;
            numArraysInFile &= 0x3F;
            if (dst == scratch)
            {
                scratch += (scratchEnd - scratch - 0xC000) >> 1;
                dstEnd = scratch;
            }
            int totalSize = 0;
            if (numArraysInFile == 0)
            {
                for (int i = 0; i < arrayCount; i++)
                {
                    byte* chunkDst = dst;
                    int dec = DecodeBytes(&chunkDst, src, srcEnd, &decodedSize, (int)(dstEnd - dst), forceMemmove, scratch, scratchEnd);
                    if (dec < 0)
                        return -1;
                    dst += decodedSize;
                    arrayLens[i] = decodedSize;
                    arrayData[i] = chunkDst;
                    src += dec;
                    totalSize += decodedSize;
                }
                *totalSizeOut = totalSize;
                return (int)(src - srcOrg);
            }
            byte** entropyArrayData = stackalloc byte*[64];
            uint* entropyArraySize = stackalloc uint[64];
            byte* scratchCur = scratch;
            for (int i = 0; i < numArraysInFile; i++)
            {
                byte* chunkDst = scratchCur;
                int dec = DecodeBytes(&chunkDst, src, srcEnd, &decodedSize, (int)(scratchEnd - scratchCur), forceMemmove, scratchCur, scratchEnd);
                if (dec < 0)
                    return -1;
                entropyArrayData[i] = chunkDst;
                entropyArraySize[i] = (uint)decodedSize;
                scratchCur += decodedSize;
                totalSize += decodedSize;
                src += dec;
            }
            *totalSizeOut = totalSize;
            if (srcEnd - src < 3)
                return -1;
            int qv = ReadU16(src);
            src += 2;
            int outSize;
            if (GetBlockSize(src, srcEnd, &outSize, totalSize) < 0)
                return -1;
            int numIndexes = outSize;
            int numLens = numIndexes - arrayCount;
            if (numLens < 1)
                return -1;
            if (scratchEnd - scratchCur < numIndexes)
                return -1;
            byte* intervalLenlog2 = scratchCur;
            scratchCur += numIndexes;
            if (scratchEnd - scratchCur < numIndexes)
                return -1;
            byte* intervalIndexes = scratchCur;
            scratchCur += numIndexes;
            if ((qv & 0x8000) != 0)
            {
                int sizeOut;
                int n = DecodeBytes(&intervalIndexes, src, srcEnd, &sizeOut, numIndexes, false, scratchCur, scratchEnd);
                if (n < 0 || sizeOut != numIndexes)
                    return -1;
                src += n;
                for (int i = 0; i < numIndexes; i++)
                {
                    int t = intervalIndexes[i];
                    intervalLenlog2[i] = (byte)(t >> 4);
                    intervalIndexes[i] = (byte)(t & 0xF);
                }
                numLens = numIndexes;
            }
            else
            {
                int lenlog2Chunksize = numIndexes - arrayCount;
                int sizeOut;
                int n = DecodeBytes(&intervalIndexes, src, srcEnd, &sizeOut, numIndexes, false, scratchCur, scratchEnd);
                if (n < 0 || sizeOut != numIndexes)
                    return -1;
                src += n;
                n = DecodeBytes(&intervalLenlog2, src, srcEnd, &sizeOut, lenlog2Chunksize, false, scratchCur, scratchEnd);
                if (n < 0 || sizeOut != lenlog2Chunksize)
                    return -1;
                src += n;
                for (int i = 0; i < lenlog2Chunksize; i++)
                    if (intervalLenlog2[i] > 16)
                        return -1;
            }
            if (scratchEnd - scratchCur < 4)
                return -1;
            scratchCur = Align(scratchCur, 4);
            if (scratchEnd - scratchCur < numLens * 4)
                return -1;
            uint* decodedIntervals = (uint*)scratchCur;
            int varbitsComplen = qv & 0x3FFF;
            if (srcEnd - src < varbitsComplen)
                return -1;
            byte* f = src;
            uint bitsF = 0;
            int bitposF = 24;
            byte* srcEndActual = src + varbitsComplen;
            byte* b = srcEndActual;
            uint bitsB = 0;
            int bitposB = 24;
            int ii;
            for (ii = 0; ii + 2 <= numLens; ii += 2)
            {
                bitsF |= Swap(ReadU32(f)) >> (24 - bitposF);
                f += (bitposF + 7) >> 3;
                bitsB |= ((uint*)b)[-1] >> (24 - bitposB);
                b -= (bitposB + 7) >> 3;
                int numbitsF = intervalLenlog2[ii + 0];
                int numbitsB = intervalLenlog2[ii + 1];
                bitsF = BitOperations.RotateLeft(bitsF | 1, numbitsF);
                bitposF += numbitsF - 8 * ((bitposF + 7) >> 3);
                bitsB = BitOperations.RotateLeft(bitsB | 1, numbitsB);
                bitposB += numbitsB - 8 * ((bitposB + 7) >> 3);
                uint valueF = bitsF & BitMasks[numbitsF];
                bitsF &= ~BitMasks[numbitsF];
                uint valueB = bitsB & BitMasks[numbitsB];
                bitsB &= ~BitMasks[numbitsB];
                decodedIntervals[ii + 0] = valueF;
                decodedIntervals[ii + 1] = valueB;
            }
            if (ii < numLens)
            {
                bitsF |= Swap(ReadU32(f)) >> (24 - bitposF);
                int numbitsF = intervalLenlog2[ii];
                bitsF = BitOperations.RotateLeft(bitsF | 1, numbitsF);
                uint valueF = bitsF & BitMasks[numbitsF];
                decodedIntervals[ii + 0] = valueF;
            }
            if (intervalIndexes[numIndexes - 1] != 0)
                return -1;
            int indi = 0, leni = 0, source;
            int incrementLeni = (qv & 0x8000) != 0 ? 1 : 0;
            for (int arri = 0; arri < arrayCount; arri++)
            {
                arrayData[arri] = dst;
                if (indi >= numIndexes)
                    return -1;
                while ((source = intervalIndexes[indi++]) != 0)
                {
                    if (source > numArraysInFile)
                        return -1;
                    if (leni >= numLens)
                        return -1;
                    int curLen = (int)decodedIntervals[leni++];
                    int bytesLeft = (int)entropyArraySize[source - 1];
                    if (curLen > bytesLeft || curLen > dstEnd - dst)
                        return -1;
                    byte* blksrc = entropyArrayData[source - 1];
                    entropyArraySize[source - 1] -= (uint)curLen;
                    entropyArrayData[source - 1] += curLen;
                    byte* dstx = dst;
                    dst += curLen;
                    Buffer.MemoryCopy(blksrc, dstx, curLen, curLen);
                }
                leni += incrementLeni;
                arrayLens[arri] = (int)(dst - arrayData[arri]);
            }
            if (indi != numIndexes || leni != numLens)
                return -1;
            for (int i = 0; i < numArraysInFile; i++)
            {
                if (entropyArraySize[i] != 0)
                    return -1;
            }
            return (int)(srcEndActual - srcOrg);
        }

        private static int DecodeRecursive(byte* src, int srcSize, byte* output, int outputSize, byte* scratch, byte* scratchEnd)
        {
            byte* srcOrg = src;
            byte* outputEnd = output + outputSize;
            byte* srcEnd = src + srcSize;
            if (srcSize < 6)
                return -1;
            int n = src[0] & 0x7F;
            if (n < 2)
                return -1;
            if ((src[0] & 0x80) == 0)
            {
                src++;
                do
                {
                    int decodedSize;
                    int dec = DecodeBytes(&output, src, srcEnd, &decodedSize, (int)(outputEnd - output), true, scratch, scratchEnd);
                    if (dec < 0)
                        return -1;
                    output += decodedSize;
                    src += dec;
                } while (--n != 0);
                if (output != outputEnd)
                    return -1;
                return (int)(src - srcOrg);
            }
            else
            {
                byte* arrayData;
                int arrayLen, decodedSize;
                int dec = DecodeMultiArray(src, srcEnd, output, outputEnd, &arrayData, &arrayLen, 1, &decodedSize, true, scratch, scratchEnd);
                if (dec < 0)
                    return -1;
                output += decodedSize;
                if (output != outputEnd)
                    return -1;
                return dec;
            }
        }

        private static int DecodeRle(byte* src, int srcSize, byte* dst, int dstSize, byte* scratch, byte* scratchEnd)
        {
            if (srcSize <= 1)
            {
                if (srcSize != 1)
                    return -1;
                new Span<byte>(dst, dstSize).Fill(src[0]);
                return 1;
            }
            byte* dstEnd = dst + dstSize;
            byte* cmdPtr = src + 1, cmdPtrEnd = src + srcSize;
            if (src[0] != 0)
            {
                byte* dstPtr = scratch;
                int decSize;
                int n = DecodeBytes(&dstPtr, src, src + srcSize, &decSize, (int)(scratchEnd - scratch), true, scratch, scratchEnd);
                if (n <= 0)
                    return -1;
                int cmdLen = srcSize - n + decSize;
                if (cmdLen > scratchEnd - scratch)
                    return -1;
                Buffer.MemoryCopy(src + n, dstPtr + decSize, srcSize - n, srcSize - n);
                cmdPtr = dstPtr;
                cmdPtrEnd = &dstPtr[cmdLen];
            }
            byte rleByte = 0;
            while (cmdPtr < cmdPtrEnd)
            {
                uint cmd = cmdPtrEnd[-1];
                if (cmd - 1 >= 0x2F)
                {
                    cmdPtrEnd--;
                    uint bytesToCopy = (uint)(-1 - (int)cmd) & 0xF;
                    uint bytesToRle = cmd >> 4;
                    if (dstEnd - dst < bytesToCopy + bytesToRle || cmdPtrEnd - cmdPtr < bytesToCopy)
                        return -1;
                    Buffer.MemoryCopy(cmdPtr, dst, bytesToCopy, bytesToCopy);
                    cmdPtr += bytesToCopy;
                    dst += bytesToCopy;
                    new Span<byte>(dst, (int)bytesToRle).Fill(rleByte);
                    dst += bytesToRle;
                }
                else if (cmd >= 0x10)
                {
                    uint data = (uint)ReadU16(cmdPtrEnd - 2) - 4096;
                    cmdPtrEnd -= 2;
                    uint bytesToCopy = data & 0x3F;
                    uint bytesToRle = data >> 6;
                    if (dstEnd - dst < bytesToCopy + bytesToRle || cmdPtrEnd - cmdPtr < bytesToCopy)
                        return -1;
                    Buffer.MemoryCopy(cmdPtr, dst, bytesToCopy, bytesToCopy);
                    cmdPtr += bytesToCopy;
                    dst += bytesToCopy;
                    new Span<byte>(dst, (int)bytesToRle).Fill(rleByte);
                    dst += bytesToRle;
                }
                else if (cmd == 1)
                {
                    rleByte = *cmdPtr++;
                    cmdPtrEnd--;
                }
                else if (cmd >= 9)
                {
                    uint bytesToRle = ((uint)ReadU16(cmdPtrEnd - 2) - 0x8FF) * 128;
                    cmdPtrEnd -= 2;
                    if (dstEnd - dst < bytesToRle)
                        return -1;
                    new Span<byte>(dst, (int)bytesToRle).Fill(rleByte);
                    dst += bytesToRle;
                }
                else
                {
                    uint bytesToCopy = ((uint)ReadU16(cmdPtrEnd - 2) - 511) * 64;
                    cmdPtrEnd -= 2;
                    if (cmdPtrEnd - cmdPtr < bytesToCopy || dstEnd - dst < bytesToCopy)
                        return -1;
                    Buffer.MemoryCopy(cmdPtr, dst, bytesToCopy, bytesToCopy);
                    dst += bytesToCopy;
                    cmdPtr += bytesToCopy;
                }
            }
            if (cmdPtrEnd != cmdPtr)
                return -1;
            if (dst != dstEnd)
                return -1;
            return srcSize;
        }

        // ---- tANS --------------------------------------------------------------------------------------------------

        private struct TansLutEnt
        {
            public uint X;
            public byte BitsX;
            public byte Symbol;
            public ushort W;
        }

        private static bool TansDecodeTable(ref BitReader bits, int lBits, byte* tansA, uint* tansB, out int aUsed, out int bUsed)
        {
            aUsed = bUsed = 0;
            bits.Refill();
            if (bits.ReadBitNoRefill() != 0)
            {
                int q = bits.ReadBitsNoRefill(3);
                int numSymbols = bits.ReadBitsNoRefill(8) + 1;
                if (numSymbols < 2)
                    return false;
                int fluff = ReadFluff(ref bits, numSymbols);
                int totalRiceValues = fluff + numSymbols;
                byte* rice = stackalloc byte[512 + 32];
                new Span<byte>(rice, 512 + 32).Clear();
                BitReader2 br2;
                br2.P = bits.P - (uint)((24 - bits.BitPos + 7) >> 3);
                br2.PEnd = bits.PEnd;
                br2.BitPos = (uint)((bits.BitPos - 24) & 7);
                if (!DecodeGolombRiceLengths(rice, totalRiceValues, ref br2))
                    return false;
                new Span<byte>(rice + totalRiceValues, 16).Clear();
                bits.BitPos = 24;
                bits.P = br2.P;
                bits.Bits = 0;
                bits.Refill();
                bits.Bits <<= (int)br2.BitPos;
                bits.BitPos += (int)br2.BitPos;
                HuffRange* range = stackalloc HuffRange[133];
                int nr = ConvertToRanges(range, numSymbols, fluff, &rice[numSymbols], ref bits);
                if (nr < 0)
                    return false;
                bits.Refill();
                uint l = 1u << lBits;
                byte* curRice = rice;
                int average = 6;
                int somesum = 0;
                byte* a = tansA;
                uint* b = tansB;
                for (int ri = 0; ri < nr; ri++)
                {
                    int symbol = range[ri].Symbol;
                    int num = range[ri].Num;
                    do
                    {
                        bits.Refill();
                        int nextra = q + *curRice++;
                        if (nextra > 15)
                            return false;
                        int v = bits.ReadBitsNoRefillZero(nextra) + (1 << nextra) - (1 << q);
                        int averageDiv4 = average >> 2;
                        int limit = 2 * averageDiv4;
                        if (v <= limit)
                            v = averageDiv4 + (-(v & 1) ^ (int)((uint)v >> 1));
                        if (limit > v)
                            limit = v;
                        v += 1;
                        average += limit - averageDiv4;
                        *a = (byte)symbol;
                        *b = (uint)((symbol << 16) + v);
                        a += v == 1 ? 1 : 0;
                        b += v >= 2 ? 1 : 0;
                        somesum += v;
                        symbol += 1;
                    } while (--num != 0);
                }
                aUsed = (int)(a - tansA);
                bUsed = (int)(b - tansB);
                return somesum == l;
            }
            else
            {
                bool* seen = stackalloc bool[256];
                new Span<bool>(seen, 256).Clear();
                uint l = 1u << lBits;
                int count = bits.ReadBitsNoRefill(3) + 1;
                int bitsPerSym = Bsr((uint)lBits) + 1;
                int maxDeltaBits = bits.ReadBitsNoRefill(bitsPerSym);
                if (maxDeltaBits == 0 || maxDeltaBits > lBits)
                    return false;
                byte* a = tansA;
                uint* b = tansB;
                int weight = 0;
                int totalWeights = 0;
                do
                {
                    bits.Refill();
                    int sym = bits.ReadBitsNoRefill(8);
                    if (seen[sym])
                        return false;
                    int delta = bits.ReadBitsNoRefill(maxDeltaBits);
                    weight += delta;
                    if (weight == 0)
                        return false;
                    seen[sym] = true;
                    if (weight == 1)
                        *a++ = (byte)sym;
                    else
                        *b++ = (uint)((sym << 16) + weight);
                    totalWeights += weight;
                } while (--count != 0);
                bits.Refill();
                int sym2 = bits.ReadBitsNoRefill(8);
                if (seen[sym2])
                    return false;
                if (l - totalWeights < weight || l - totalWeights <= 1)
                    return false;
                *b++ = (uint)((sym2 << 16) + (int)(l - totalWeights));
                aUsed = (int)(a - tansA);
                bUsed = (int)(b - tansB);
                SortBytes(tansA, aUsed);
                SortUints(tansB, bUsed);
                return true;
            }
        }

        private static void SortBytes(byte* p, int n)
        {
            for (int i = 1; i < n; i++)
            {
                byte t = p[i];
                int j = i;
                for (; j > 0 && t < p[j - 1]; j--)
                    p[j] = p[j - 1];
                p[j] = t;
            }
        }

        private static void SortUints(uint* p, int n)
        {
            for (int i = 1; i < n; i++)
            {
                uint t = p[i];
                int j = i;
                for (; j > 0 && t < p[j - 1]; j--)
                    p[j] = p[j - 1];
                p[j] = t;
            }
        }

        private static void TansInitLut(byte* tansA, int aUsed, uint* tansB, int bUsed, int lBits, TansLutEnt* lut)
        {
            TansLutEnt** pointers = stackalloc TansLutEnt*[4];
            int l = 1 << lBits;
            uint slotsLeft = (uint)(l - aUsed);
            uint sa = slotsLeft >> 2;
            pointers[0] = lut;
            uint sb = sa + ((slotsLeft & 3) > 0 ? 1u : 0u);
            pointers[1] = lut + sb;
            sb += sa + ((slotsLeft & 3) > 1 ? 1u : 0u);
            pointers[2] = lut + sb;
            sb += sa + ((slotsLeft & 3) > 2 ? 1u : 0u);
            pointers[3] = lut + sb;
            {
                TansLutEnt* singles = lut + slotsLeft;
                TansLutEnt le = new TansLutEnt { W = 0, BitsX = (byte)lBits, X = (1u << lBits) - 1 };
                for (int i = 0; i < aUsed; i++)
                {
                    singles[i] = le;
                    singles[i].Symbol = tansA[i];
                }
            }
            int weightsSum = 0;
            for (int i = 0; i < bUsed; i++)
            {
                int weight = (int)(tansB[i] & 0xFFFF);
                int symbol = (int)(tansB[i] >> 16);
                if (weight > 4)
                {
                    int symBits = Bsr((uint)weight);
                    int z = lBits - symBits;
                    TansLutEnt le;
                    le.Symbol = (byte)symbol;
                    le.BitsX = (byte)z;
                    le.X = (1u << z) - 1;
                    le.W = (ushort)((l - 1) & (weight << z));
                    int whatToAdd = 1 << z;
                    int x = (1 << (symBits + 1)) - weight;
                    for (int j = 0; j < 4; j++)
                    {
                        TansLutEnt* dst = pointers[j];
                        int y = (weight + ((weightsSum - j - 1) & 3)) >> 2;
                        if (x >= y)
                        {
                            for (int n = y; n != 0; n--)
                            {
                                *dst++ = le;
                                le.W = (ushort)(le.W + whatToAdd);
                            }
                            x -= y;
                        }
                        else
                        {
                            for (int n = x; n != 0; n--)
                            {
                                *dst++ = le;
                                le.W = (ushort)(le.W + whatToAdd);
                            }
                            z--;
                            whatToAdd >>= 1;
                            le.BitsX = (byte)z;
                            le.W = 0;
                            le.X >>= 1;
                            for (int n = y - x; n != 0; n--)
                            {
                                *dst++ = le;
                                le.W = (ushort)(le.W + whatToAdd);
                            }
                            x = weight;
                        }
                        pointers[j] = dst;
                    }
                }
                else
                {
                    uint bitsv = (uint)(((1 << weight) - 1) << (weightsSum & 3));
                    bitsv |= bitsv >> 4;
                    int n = weight, ww = weight;
                    do
                    {
                        int idx = BitOperations.TrailingZeroCount(bitsv);
                        bitsv &= bitsv - 1;
                        TansLutEnt* dst = pointers[idx]++;
                        dst->Symbol = (byte)symbol;
                        int weightBits = Bsr((uint)ww);
                        dst->BitsX = (byte)(lBits - weightBits);
                        dst->X = (1u << (lBits - weightBits)) - 1;
                        dst->W = (ushort)((l - 1) & (ww++ << (lBits - weightBits)));
                    } while (--n != 0);
                }
                weightsSum += weight;
            }
        }

        private static int DecodeTans(byte* src, int srcSize, byte* dst, int dstSize, byte* scratch, byte* scratchEnd)
        {
            if (srcSize < 8 || dstSize < 5)
                return -1;
            byte* srcEnd = src + srcSize;
            BitReader br = new BitReader { BitPos = 24, Bits = 0, P = src, PEnd = srcEnd };
            br.Refill();
            if (br.ReadBitNoRefill() != 0)
                return -1;
            int lBits = br.ReadBitsNoRefill(2) + 8;
            byte* tansA = stackalloc byte[256];
            uint* tansB = stackalloc uint[256];
            int aUsed, bUsed;
            if (!TansDecodeTable(ref br, lBits, tansA, tansB, out aUsed, out bUsed))
                return -1;
            src = br.P - (24 - br.BitPos) / 8;
            if (src >= srcEnd)
                return -1;
            long lutSpace = ((sizeof(TansLutEnt) << lBits) + 15) & ~15;
            if (lutSpace > scratchEnd - scratch)
                return -1;
            TansLutEnt* lut = (TansLutEnt*)Align(scratch, 16);
            TansInitLut(tansA, aUsed, tansB, bUsed, lBits, lut);
            byte* dstEnd = dst + dstSize - 5;
            uint lMask = (1u << lBits) - 1;
            uint bitsF = ReadU32(src);
            src += 4;
            uint bitsB = Swap(ReadU32(srcEnd - 4));
            srcEnd -= 4;
            int bitposF = 32, bitposB = 32;
            uint s0 = bitsF & lMask;
            uint s1 = bitsB & lMask;
            bitsF >>= lBits; bitposF -= lBits;
            bitsB >>= lBits; bitposB -= lBits;
            uint s2 = bitsF & lMask;
            uint s3 = bitsB & lMask;
            bitsF >>= lBits; bitposF -= lBits;
            bitsB >>= lBits; bitposB -= lBits;
            bitsF |= ReadU32(src) << bitposF;
            src += (31 - bitposF) >> 3;
            bitposF |= 24;
            uint s4 = bitsF & lMask;
            bitsF >>= lBits; bitposF -= lBits;
            byte* ptrF = src - (bitposF >> 3);
            bitposF &= 7;
            byte* ptrB = srcEnd + (bitposB >> 3);
            bitposB &= 7;
            if (ptrF > ptrB)
                return -1;
            uint* st = stackalloc uint[5];
            st[0] = s0; st[1] = s1; st[2] = s2; st[3] = s3; st[4] = s4;
            if (dst < dstEnd)
            {
                for (;;)
                {
                    bitsF |= ReadU32(ptrF) << bitposF;
                    ptrF += (31 - bitposF) >> 3;
                    bitposF |= 24;
                    if (TansRound(lut, st, 0, ref dst, dstEnd, ref bitsF, ref bitposF)) break;
                    if (TansRound(lut, st, 1, ref dst, dstEnd, ref bitsF, ref bitposF)) break;
                    bitsF |= ReadU32(ptrF) << bitposF;
                    ptrF += (31 - bitposF) >> 3;
                    bitposF |= 24;
                    if (TansRound(lut, st, 2, ref dst, dstEnd, ref bitsF, ref bitposF)) break;
                    if (TansRound(lut, st, 3, ref dst, dstEnd, ref bitsF, ref bitposF)) break;
                    bitsF |= ReadU32(ptrF) << bitposF;
                    ptrF += (31 - bitposF) >> 3;
                    bitposF |= 24;
                    if (TansRound(lut, st, 4, ref dst, dstEnd, ref bitsF, ref bitposF)) break;
                    bitsB |= Swap(((uint*)ptrB)[-1]) << bitposB;
                    ptrB -= (31 - bitposB) >> 3;
                    bitposB |= 24;
                    if (TansRound(lut, st, 0, ref dst, dstEnd, ref bitsB, ref bitposB)) break;
                    if (TansRound(lut, st, 1, ref dst, dstEnd, ref bitsB, ref bitposB)) break;
                    bitsB |= Swap(((uint*)ptrB)[-1]) << bitposB;
                    ptrB -= (31 - bitposB) >> 3;
                    bitposB |= 24;
                    if (TansRound(lut, st, 2, ref dst, dstEnd, ref bitsB, ref bitposB)) break;
                    if (TansRound(lut, st, 3, ref dst, dstEnd, ref bitsB, ref bitposB)) break;
                    bitsB |= Swap(((uint*)ptrB)[-1]) << bitposB;
                    ptrB -= (31 - bitposB) >> 3;
                    bitposB |= 24;
                    if (TansRound(lut, st, 4, ref dst, dstEnd, ref bitsB, ref bitposB)) break;
                }
            }
            if (ptrB - ptrF + (bitposF >> 3) + (bitposB >> 3) != 0)
                return -1;
            uint statesOr = st[0] | st[1] | st[2] | st[3] | st[4];
            if ((statesOr & ~0xFFu) != 0)
                return -1;
            dstEnd[0] = (byte)st[0];
            dstEnd[1] = (byte)st[1];
            dstEnd[2] = (byte)st[2];
            dstEnd[3] = (byte)st[3];
            dstEnd[4] = (byte)st[4];
            return srcSize;
        }

        // One tANS step; true when the output is full.
        private static bool TansRound(TansLutEnt* lut, uint* st, int i, ref byte* dst, byte* dstEnd, ref uint bits, ref int bitpos)
        {
            TansLutEnt* e = &lut[st[i]];
            *dst++ = e->Symbol;
            bitpos -= e->BitsX;
            st[i] = (bits & e->X) + e->W;
            bits >>= e->BitsX;
            return dst >= dstEnd;
        }

        private static int GetBlockSize(byte* src, byte* srcEnd, int* destSize, int destCapacity)
        {
            byte* srcOrg = src;
            int srcSize, dstSize;
            if (srcEnd - src < 2)
                return -1;
            int chunkType = (src[0] >> 4) & 0x7;
            if (chunkType == 0)
            {
                if (src[0] >= 0x80)
                {
                    srcSize = ((src[0] << 8) | src[1]) & 0xFFF;
                    src += 2;
                }
                else
                {
                    if (srcEnd - src < 3)
                        return -1;
                    srcSize = (src[0] << 16) | (src[1] << 8) | src[2];
                    if ((srcSize & ~0x3FFFF) != 0)
                        return -1;
                    src += 3;
                }
                if (srcSize > destCapacity || srcEnd - src < srcSize)
                    return -1;
                *destSize = srcSize;
                return (int)(src + srcSize - srcOrg);
            }
            if (chunkType >= 6)
                return -1;
            if (src[0] >= 0x80)
            {
                if (srcEnd - src < 3)
                    return -1;
                uint bits = (uint)((src[0] << 16) | (src[1] << 8) | src[2]);
                srcSize = (int)(bits & 0x3FF);
                dstSize = srcSize + (int)((bits >> 10) & 0x3FF) + 1;
                src += 3;
            }
            else
            {
                if (srcEnd - src < 5)
                    return -1;
                uint bits = (uint)((src[1] << 24) | (src[2] << 16) | (src[3] << 8) | src[4]);
                srcSize = (int)(bits & 0x3FFFF);
                dstSize = (int)((((bits >> 18) | ((uint)src[0] << 14)) & 0x3FFFF) + 1);
                if (srcSize >= dstSize)
                    return -1;
                src += 5;
            }
            if (srcEnd - src < srcSize || dstSize > destCapacity)
                return -1;
            *destSize = dstSize;
            return srcSize;
        }

        private static int DecodeBytes(byte** output, byte* src, byte* srcEnd, int* decodedSize, long outputSize, bool forceMemmove, byte* scratch, byte* scratchEnd)
        {
            byte* srcOrg = src;
            int srcSize, dstSize;
            if (srcEnd - src < 2)
                return -1;
            int chunkType = (src[0] >> 4) & 0x7;
            if (chunkType == 0)
            {
                if (src[0] >= 0x80)
                {
                    srcSize = ((src[0] << 8) | src[1]) & 0xFFF;
                    src += 2;
                }
                else
                {
                    if (srcEnd - src < 3)
                        return -1;
                    srcSize = (src[0] << 16) | (src[1] << 8) | src[2];
                    if ((srcSize & ~0x3FFFF) != 0)
                        return -1;
                    src += 3;
                }
                if (srcSize > outputSize || srcEnd - src < srcSize)
                    return -1;
                *decodedSize = srcSize;
                if (forceMemmove)
                    Buffer.MemoryCopy(src, *output, srcSize, srcSize);
                else
                    *output = src;
                return (int)(src + srcSize - srcOrg);
            }
            if (src[0] >= 0x80)
            {
                if (srcEnd - src < 3)
                    return -1;
                uint bits = (uint)((src[0] << 16) | (src[1] << 8) | src[2]);
                srcSize = (int)(bits & 0x3FF);
                dstSize = srcSize + (int)((bits >> 10) & 0x3FF) + 1;
                src += 3;
            }
            else
            {
                if (srcEnd - src < 5)
                    return -1;
                uint bits = (uint)((src[1] << 24) | (src[2] << 16) | (src[3] << 8) | src[4]);
                srcSize = (int)(bits & 0x3FFFF);
                dstSize = (int)((((bits >> 18) | ((uint)src[0] << 14)) & 0x3FFFF) + 1);
                if (srcSize >= dstSize)
                    return -1;
                src += 5;
            }
            if (srcEnd - src < srcSize || dstSize > outputSize)
                return -1;
            byte* dst = *output;
            if (dst == scratch)
            {
                if (scratchEnd - scratch < dstSize)
                    return -1;
                scratch += dstSize;
            }
            int srcUsed = -1;
            switch (chunkType)
            {
                case 2:
                case 4:
                    srcUsed = DecodeBytesType12(src, srcSize, dst, dstSize, chunkType >> 1);
                    break;
                case 5:
                    srcUsed = DecodeRecursive(src, srcSize, dst, dstSize, scratch, scratchEnd);
                    break;
                case 3:
                    srcUsed = DecodeRle(src, srcSize, dst, dstSize, scratch, scratchEnd);
                    break;
                case 1:
                    srcUsed = DecodeTans(src, srcSize, dst, dstSize, scratch, scratchEnd);
                    break;
            }
            if (srcUsed != srcSize)
                return -1;
            *decodedSize = dstSize;
            return (int)(src + srcSize - srcOrg);
        }

        // ---- LZ layer ----------------------------------------------------------------------------------------------

        private static bool UnpackOffsets(byte* src, byte* srcEnd, byte* packedOffs, byte* packedOffsExtra, int packedOffsSize, int multiDistScale, byte* packedLitlen, int packedLitlenSize, int* offsStream, int* lenStream)
        {
            BitReader a = new BitReader { BitPos = 24, Bits = 0, P = src, PEnd = srcEnd };
            a.Refill();
            BitReader b = new BitReader { BitPos = 24, Bits = 0, P = srcEnd, PEnd = src };
            b.RefillBackwards();
            if (b.Bits < 0x2000)
                return false;
            int n = Clz(b.Bits);
            b.BitPos += n;
            b.Bits <<= n;
            b.RefillBackwards();
            n++;
            int u32LenSize = (int)(b.Bits >> (32 - n)) - 1;
            b.BitPos += n;
            b.Bits <<= n;
            b.RefillBackwards();
            if (multiDistScale == 0)
            {
                byte* end = packedOffs + packedOffsSize;
                while (packedOffs != end)
                {
                    *offsStream++ = -(int)a.ReadDistance(*packedOffs++, false);
                    if (packedOffs == end)
                        break;
                    *offsStream++ = -(int)b.ReadDistance(*packedOffs++, true);
                }
            }
            else
            {
                int* org = offsStream;
                byte* end = packedOffs + packedOffsSize;
                while (packedOffs != end)
                {
                    uint cmd = *packedOffs++;
                    if ((cmd >> 3) > 26)
                        return false;
                    uint offs = ((8 + (cmd & 7)) << (int)(cmd >> 3)) | a.ReadMoreThan24Bits((int)(cmd >> 3));
                    *offsStream++ = 8 - (int)offs;
                    if (packedOffs == end)
                        break;
                    cmd = *packedOffs++;
                    if ((cmd >> 3) > 26)
                        return false;
                    offs = ((8 + (cmd & 7)) << (int)(cmd >> 3)) | b.ReadMoreThan24BitsB((int)(cmd >> 3));
                    *offsStream++ = 8 - (int)offs;
                }
                if (multiDistScale != 1)
                {
                    long count = offsStream - org;
                    for (long i = 0; i < count; i++)
                        org[i] = multiDistScale * org[i] - packedOffsExtra[i];
                }
            }
            if (u32LenSize > 512 || u32LenSize < 0)
                return false;
            uint* u32 = stackalloc uint[512];
            int k;
            for (k = 0; k + 1 < u32LenSize; k += 2)
            {
                if (!a.ReadLength(out u32[k], false))
                    return false;
                if (!b.ReadLength(out u32[k + 1], true))
                    return false;
            }
            if (k < u32LenSize)
            {
                if (!a.ReadLength(out u32[k], false))
                    return false;
            }
            a.P -= (24 - a.BitPos) >> 3;
            b.P += (24 - b.BitPos) >> 3;
            if (a.P != b.P)
                return false;
            int u = 0;
            for (int i = 0; i < packedLitlenSize; i++)
            {
                uint v = packedLitlen[i];
                if (v == 255)
                {
                    if (u >= u32LenSize)
                        return false;
                    v = u32[u++] + 255;
                }
                lenStream[i] = (int)(v + 3);
            }
            return u == u32LenSize;
        }

        private sealed class KrakenLzTable
        {
            public byte* CmdStream;
            public int CmdStreamSize;
            public int* OffsStream;
            public int OffsStreamSize;
            public byte* LitStream;
            public int LitStreamSize;
            public int* LenStream;
            public int LenStreamSize;
        }

        private static bool KrakenReadLzTable(int mode, byte* src, byte* srcEnd, byte* dst, int dstSize, int offset, byte* scratch, byte* scratchEnd, KrakenLzTable lzt)
        {
            int decodeCount, n;
            if (mode > 1)
                return false;
            if (srcEnd - src < 13)
                return false;
            if (offset == 0)
            {
                Copy64(dst, src);
                dst += 8;
                src += 8;
            }
            if ((*src & 0x80) != 0)
                return false;
            byte* o = scratch;
            n = DecodeBytes(&o, src, srcEnd, &decodeCount, Math.Min(scratchEnd - scratch, dstSize), true, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            lzt.LitStream = o;
            lzt.LitStreamSize = decodeCount;
            scratch += decodeCount;
            o = scratch;
            n = DecodeBytes(&o, src, srcEnd, &decodeCount, Math.Min(scratchEnd - scratch, dstSize), true, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            lzt.CmdStream = o;
            lzt.CmdStreamSize = decodeCount;
            scratch += decodeCount;
            if (srcEnd - src < 3)
                return false;
            int offsScaling = 0;
            byte* packedOffsExtra = null;
            byte* packedOffs;
            int offsSize;
            if ((src[0] & 0x80) != 0)
            {
                offsScaling = src[0] - 127;
                src++;
                packedOffs = scratch;
                n = DecodeBytes(&packedOffs, src, srcEnd, &offsSize, Math.Min(scratchEnd - scratch, lzt.CmdStreamSize), false, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                scratch += offsSize;
                if (offsScaling != 1)
                {
                    packedOffsExtra = scratch;
                    n = DecodeBytes(&packedOffsExtra, src, srcEnd, &decodeCount, Math.Min(scratchEnd - scratch, offsSize), false, scratch, scratchEnd);
                    if (n < 0 || decodeCount != offsSize)
                        return false;
                    src += n;
                    scratch += decodeCount;
                }
            }
            else
            {
                packedOffs = scratch;
                n = DecodeBytes(&packedOffs, src, srcEnd, &offsSize, Math.Min(scratchEnd - scratch, lzt.CmdStreamSize), false, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                scratch += offsSize;
            }
            lzt.OffsStreamSize = offsSize;
            byte* packedLen = scratch;
            int lenSize;
            n = DecodeBytes(&packedLen, src, srcEnd, &lenSize, Math.Min(scratchEnd - scratch, dstSize >> 2), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += lenSize;
            lzt.LenStreamSize = lenSize;
            scratch = Align(scratch, 16);
            lzt.OffsStream = (int*)scratch;
            scratch += offsSize * 4;
            scratch = Align(scratch, 16);
            lzt.LenStream = (int*)scratch;
            scratch += lenSize * 4;
            if (scratch + 64 > scratchEnd)
                return false;
            return UnpackOffsets(src, srcEnd, packedOffs, packedOffsExtra, offsSize, offsScaling, packedLen, lenSize, lzt.OffsStream, lzt.LenStream);
        }

        private static bool KrakenProcessLzRuns(int mode, byte* dstBase, int dstSize, int offset, KrakenLzTable lzt)
        {
            byte* dst = dstBase + (offset == 0 ? 8 : 0);
            byte* dstEnd = dstBase + dstSize;
            byte* dstStart = dstBase - offset;
            byte* cmd = lzt.CmdStream, cmdEnd = cmd + lzt.CmdStreamSize;
            int* lenStream = lzt.LenStream, lenEnd = lzt.LenStream + lzt.LenStreamSize;
            byte* lit = lzt.LitStream, litEnd = lzt.LitStream + lzt.LitStreamSize;
            int* offs = lzt.OffsStream, offsEnd = lzt.OffsStream + lzt.OffsStreamSize;
            int* recent = stackalloc int[7];
            recent[3] = recent[4] = recent[5] = -8;
            int lastOffset = -8;
            bool sub = mode == 0;
            while (cmd < cmdEnd)
            {
                uint f = *cmd++;
                uint litlen = f & 3;
                uint offsIndex = f >> 6;
                uint matchlen = (f >> 2) & 0xF;
                uint nextLong = (uint)*lenStream;
                if (litlen == 3)
                {
                    lenStream++;
                    litlen = nextLong;
                }
                recent[6] = *offs;
                if (dstEnd - dst < litlen)
                    return false;
                if (sub)
                {
                    for (uint i = 0; i < litlen; i++)
                        dst[i] = (byte)(lit[i] + dst[(long)i + lastOffset]);
                }
                else
                {
                    Buffer.MemoryCopy(lit, dst, litlen, litlen);
                }
                dst += litlen;
                lit += litlen;
                int offset2 = recent[offsIndex + 3];
                recent[offsIndex + 3] = recent[offsIndex + 2];
                recent[offsIndex + 2] = recent[offsIndex + 1];
                recent[offsIndex + 1] = recent[offsIndex + 0];
                recent[3] = offset2;
                lastOffset = offset2;
                if (offsIndex == 3)
                    offs++;
                if ((ulong)(long)offset2 < (ulong)(dstStart - dst))
                    return false;
                byte* from = dst + offset2;
                if (matchlen != 15)
                {
                    matchlen += 2;
                }
                else
                {
                    matchlen = (uint)(14 + *lenStream++);
                }
                if (matchlen > dstEnd - dst)
                    return false;
                CopyMatch(dst, from, (int)matchlen);
                dst += matchlen;
            }
            if (offs != offsEnd || lenStream != lenEnd)
                return false;
            long finalLen = dstEnd - dst;
            if (finalLen != litEnd - lit)
                return false;
            if (sub)
            {
                for (long i = 0; i < finalLen; i++)
                    dst[i] = (byte)(lit[i] + dst[i + lastOffset]);
            }
            else
            {
                Buffer.MemoryCopy(lit, dst, finalLen, finalLen);
            }
            return true;
        }

        // An LZ match: copied in 8-byte steps like the reference decoder (offsets are at least 8 back in valid data,
        // so this equals a byte-wise copy).
        private static void CopyMatch(byte* dst, byte* from, int len)
        {
            if (dst - from >= 8)
            {
                int i = 0;
                for (; i + 8 <= len; i += 8)
                    Copy64(dst + i, from + i);
                for (; i < len; i++)
                    dst[i] = from[i];
            }
            else
            {
                for (int i = 0; i < len; i++)
                    dst[i] = from[i];
            }
        }

        private sealed class LeviathanLzTable
        {
            public int* OffsStream;
            public int OffsStreamSize;
            public int* LenStream;
            public int LenStreamSize;
            public byte*[] LitStream = new byte*[16];
            public int[] LitStreamSize = new int[16];
            public int LitStreamTotal;
            public byte*[] MultiCmdPtr = new byte*[8];
            public byte*[] MultiCmdEnd = new byte*[8];
            public byte* CmdStream;
            public int CmdStreamSize;
        }

        private static bool LeviathanReadLzTable(int chunkType, byte* src, byte* srcEnd, byte* dst, int dstSize, int offset, byte* scratch, byte* scratchEnd, LeviathanLzTable lzt)
        {
            int decodeCount, n;
            if (chunkType > 5)
                return false;
            if (srcEnd - src < 13)
                return false;
            if (offset == 0)
            {
                Copy64(dst, src);
                dst += 8;
                src += 8;
            }
            int offsScaling = 0;
            byte* packedOffsExtra = null;
            int offsLimit = dstSize / 3;
            byte* packedOffs = scratch;
            int offsSize;
            if ((src[0] & 0x80) == 0)
            {
                n = DecodeBytes(&packedOffs, src, srcEnd, &offsSize, Math.Min(scratchEnd - scratch, offsLimit), false, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                scratch += offsSize;
            }
            else
            {
                offsScaling = src[0] - 127;
                src++;
                n = DecodeBytes(&packedOffs, src, srcEnd, &offsSize, Math.Min(scratchEnd - scratch, offsLimit), false, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                scratch += offsSize;
                if (offsScaling != 1)
                {
                    packedOffsExtra = scratch;
                    n = DecodeBytes(&packedOffsExtra, src, srcEnd, &decodeCount, Math.Min(scratchEnd - scratch, offsLimit), false, scratch, scratchEnd);
                    if (n < 0 || decodeCount != offsSize)
                        return false;
                    src += n;
                    scratch += decodeCount;
                }
            }
            lzt.OffsStreamSize = offsSize;
            byte* packedLen = scratch;
            int lenSize;
            n = DecodeBytes(&packedLen, src, srcEnd, &lenSize, Math.Min(scratchEnd - scratch, dstSize / 5), false, scratch, scratchEnd);
            if (n < 0)
                return false;
            src += n;
            scratch += lenSize;
            lzt.LenStreamSize = lenSize;
            scratch = Align(scratch, 16);
            lzt.OffsStream = (int*)scratch;
            scratch += offsSize * 4;
            scratch = Align(scratch, 16);
            lzt.LenStream = (int*)scratch;
            scratch += lenSize * 4;
            if (scratch > scratchEnd)
                return false;
            if (chunkType <= 1)
            {
                byte* o = scratch;
                n = DecodeBytes(&o, src, srcEnd, &decodeCount, Math.Min(scratchEnd - scratch, dstSize), true, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                lzt.LitStream[0] = o;
                lzt.LitStreamSize[0] = decodeCount;
            }
            else
            {
                int arrayCount = chunkType == 2 ? 2 : chunkType == 3 ? 4 : 16;
                byte** arr = stackalloc byte*[16];
                int* lens = stackalloc int[16];
                n = DecodeMultiArray(src, srcEnd, scratch, scratchEnd, arr, lens, arrayCount, &decodeCount, true, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                for (int i = 0; i < 16; i++)
                {
                    lzt.LitStream[i] = arr[i];
                    lzt.LitStreamSize[i] = lens[i];
                }
            }
            scratch += decodeCount;
            lzt.LitStreamTotal = decodeCount;
            if (src >= srcEnd)
                return false;
            if ((src[0] & 0x80) == 0)
            {
                byte* o = scratch;
                n = DecodeBytes(&o, src, srcEnd, &decodeCount, Math.Min(scratchEnd - scratch, dstSize), true, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                lzt.CmdStream = o;
                lzt.CmdStreamSize = decodeCount;
                scratch += decodeCount;
            }
            else
            {
                if (src[0] != 0x83)
                    return false;
                src++;
                byte** arr = stackalloc byte*[8];
                int* lens = stackalloc int[8];
                n = DecodeMultiArray(src, srcEnd, scratch, scratchEnd, arr, lens, 8, &decodeCount, true, scratch, scratchEnd);
                if (n < 0)
                    return false;
                src += n;
                for (int i = 0; i < 8; i++)
                {
                    lzt.MultiCmdPtr[i] = arr[i];
                    lzt.MultiCmdEnd[i] = arr[i] + lens[i];
                }
                lzt.CmdStream = null;
                lzt.CmdStreamSize = decodeCount;
                scratch += decodeCount;
            }
            if (dstSize > scratchEnd - scratch)
                return false;
            return UnpackOffsets(src, srcEnd, packedOffs, packedOffsExtra, offsSize, offsScaling, packedLen, lenSize, lzt.OffsStream, lzt.LenStream);
        }

        // The literal modes of Leviathan's chunk types 0..5.
        private sealed class LitState
        {
            public int Mode;
            public byte*[] Streams = new byte*[16];
            public byte[] NextLit = new byte[16];
            public byte* ChunkStart;

            public LitState(int mode, LeviathanLzTable lzt, byte* chunkStart)
            {
                Mode = mode;
                ChunkStart = chunkStart;
                if (mode == 3 || mode == 5)
                {
                    int num = mode == 3 ? 4 : 16;
                    // stream for a byte at p is lit_stream[(p - chunkStart) & mask]
                    for (int i = 0; i < num; i++)
                        Streams[i] = lzt.LitStream[i];
                }
                else if (mode == 4)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        byte* p = lzt.LitStream[i];
                        NextLit[i] = *p;
                        Streams[i] = p + 1;
                    }
                }
                else
                {
                    Streams[0] = lzt.LitStream[0];
                    Streams[1] = lzt.LitStream[1];
                }
            }

            public void Copy(byte* dst, long litlen, long lastOffset)
            {
                switch (Mode)
                {
                    case 0:
                    {
                        byte* l = Streams[0];
                        for (long i = 0; i < litlen; i++)
                            dst[i] = (byte)(l[i] + dst[i + lastOffset]);
                        Streams[0] = l + litlen;
                        break;
                    }
                    case 1:
                        Buffer.MemoryCopy(Streams[0], dst, litlen, litlen);
                        Streams[0] += litlen;
                        break;
                    case 2:
                    {
                        if (litlen == 0)
                            return;
                        dst[0] = (byte)(*Streams[1]++ + dst[lastOffset]);
                        byte* l = Streams[0];
                        for (long i = 1; i < litlen; i++)
                            dst[i] = (byte)(l[i - 1] + dst[i + lastOffset]);
                        Streams[0] = l + (litlen - 1);
                        break;
                    }
                    case 3:
                    case 5:
                    {
                        int mask = Mode == 3 ? 3 : 15;
                        for (long i = 0; i < litlen; i++)
                        {
                            byte* p = dst + i;
                            int s = (int)((p - ChunkStart) & mask);
                            p[0] = (byte)(*Streams[s]++ + p[lastOffset]);
                        }
                        break;
                    }
                    case 4:
                    {
                        int context = dst[-1];
                        for (long i = 0; i < litlen; i++)
                        {
                            int slot = context >> 4;
                            context = NextLit[slot];
                            dst[i] = (byte)context;
                            NextLit[slot] = *Streams[slot]++;
                        }
                        break;
                    }
                }
            }
        }

        private static bool LeviathanProcessLzRuns(int chunkType, byte* dstBase, int dstSize, int offset, LeviathanLzTable lzt)
        {
            if (chunkType > 5)
                return false;
            byte* dst = dstBase + (offset == 0 ? 8 : 0);
            byte* dstEnd = dstBase + dstSize;
            byte* windowBase = dstBase - offset;
            byte* chunkStart = dstBase;
            bool multi = lzt.CmdStream == null;
            byte* cmdStream = lzt.CmdStream, cmdEnd = lzt.CmdStream + lzt.CmdStreamSize;
            int* lenStream = lzt.LenStream, lenEnd = lzt.LenStream + lzt.LenStreamSize;
            int* offs = lzt.OffsStream, offsEnd = lzt.OffsStream + lzt.OffsStreamSize;
            byte* matchZoneEnd = dstEnd - chunkStart >= 16 ? dstEnd - 16 : chunkStart;
            int* recent = stackalloc int[16];
            for (int i = 8; i < 15; i++)
                recent[i] = -8;
            long lastOffset = -8;
            LitState lits = new LitState(chunkType, lzt, chunkStart);
            byte*[] multiCmd = new byte*[8];
            int cmdLeft = 0;
            int cur = 0;
            if (multi)
            {
                for (int i = 0; i < 8; i++)
                    multiCmd[i] = lzt.MultiCmdPtr[i];
                cmdLeft = lzt.CmdStreamSize;
                cur = (int)((dst - chunkStart) & 7);
            }
            for (;;)
            {
                uint cmd;
                if (!multi)
                {
                    if (cmdStream >= cmdEnd)
                        break;
                    cmd = *cmdStream++;
                }
                else
                {
                    if (cmdLeft == 0)
                        break;
                    cmdLeft--;
                    if (multiCmd[cur] >= lzt.MultiCmdEnd[cur])
                        return false;
                    cmd = *multiCmd[cur]++;
                }
                uint offsIndex = cmd >> 5;
                uint matchlen = (cmd & 7) + 2;
                recent[15] = offs < offsEnd ? *offs : 0;
                // literals before the match
                uint litCmd = (cmd >> 3) & 3;
                long litlen;
                if (litCmd == 3)
                {
                    if (lenStream >= lenEnd)
                        return false;
                    litlen = chunkType == 4 ? *lenStream++ : (*lenStream++ & 0xFFFFFF);
                    if (chunkType == 4 && litlen <= 0)
                        return false;
                }
                else
                {
                    litlen = litCmd;
                }
                if (chunkType == 2 && litCmd == 0)
                    litlen = 0;
                if (litlen > matchZoneEnd - dst && litlen > dstEnd - dst)
                    return false;
                if (litlen > dstEnd - dst)
                    return false;
                lits.Copy(dst, litlen, lastOffset);
                dst += litlen;
                long offset2 = recent[offsIndex + 8];
                for (int i = (int)offsIndex + 8; i > 8; i--)
                    recent[i] = recent[i - 1];
                recent[8] = (int)offset2;
                if (offsIndex == 7)
                    offs++;
                lastOffset = offset2;
                if (offset2 >= 0 || -offset2 > dst - windowBase)
                    return false;
                byte* from = dst + offset2;
                if (matchlen == 9)
                {
                    if (lenStream >= lenEnd)
                        return false;
                    matchlen = (uint)(*--lenEnd + 6);
                }
                if (matchlen > dstEnd - dst)
                    return false;
                CopyMatch(dst, from, (int)matchlen);
                dst += matchlen;
                if (multi)
                    cur = (int)((dst - chunkStart) & 7);
            }
            if (offs != offsEnd || lenStream != lenEnd)
                return false;
            if (dst < dstEnd)
            {
                long finalLen = dstEnd - dst;
                if (chunkType == 2)
                {
                    // lamsub: the first final literal is from the lam stream
                    dst[0] = (byte)(*lits.Streams[1]++ + dst[lastOffset]);
                    byte* l = lits.Streams[0];
                    for (long i = 1; i < finalLen; i++)
                        dst[i] = (byte)(l[i - 1] + dst[i + lastOffset]);
                }
                else
                {
                    lits.Copy(dst, finalLen, lastOffset);
                }
            }
            else if (dst != dstEnd)
            {
                return false;
            }
            return true;
        }
    }
}
