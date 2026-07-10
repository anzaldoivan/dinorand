using System;
using System.IO;

namespace DinoRand.FileFormats.SevenZip;

/// <summary>
/// Decode-only LZMA / LZMA2 implementation — a C# port of the reference decoder from Igor Pavlov's
/// LZMA SDK (public domain). Vendored so DinoRand can read Classic REbirth's embedded 7z resource
/// (docs/reference/dc1/puzzle/REBIRTH-DDRAW-TEXT-STORE-RE.md) without a NuGet dependency; no encoder
/// is needed anywhere (the rebuilt archive stores modified files with the 7z Copy codec).
///
/// <para>Decodes into a caller-supplied output buffer of exactly the known unpacked size (the 7z
/// header supplies it); the output buffer doubles as the LZ window. Every stream DinoRand decodes is
/// CRC32-verified against the archive's own digest table by the caller, so a decode defect cannot
/// pass silently.</para>
/// </summary>
internal sealed class Lzma
{
    // ---- range decoder ----
    private const int KNumBitModelTotalBits = 11;
    private const ushort KBitModelTotal = 1 << KNumBitModelTotalBits; // 2048
    private const int KNumMoveBits = 5;
    private const uint KTopValue = 1u << 24;

    private byte[] _in = Array.Empty<byte>();
    private int _inPos, _inEnd;
    private uint _range, _code;

    private void RcInit()
    {
        if (_inPos + 5 > _inEnd || _in[_inPos] != 0)
            throw new InvalidDataException("LZMA: bad range-coder init byte");
        _inPos++;
        _code = 0;
        _range = 0xFFFFFFFF;
        for (int i = 0; i < 4; i++) _code = (_code << 8) | NextIn();
    }

    private byte NextIn()
    {
        if (_inPos >= _inEnd) throw new InvalidDataException("LZMA: input overrun");
        return _in[_inPos++];
    }

    private void Normalize()
    {
        if (_range < KTopValue)
        {
            _range <<= 8;
            _code = (_code << 8) | NextIn();
        }
    }

    private uint DecodeBit(ushort[] probs, int index)
    {
        uint bound = (_range >> KNumBitModelTotalBits) * probs[index];
        uint symbol;
        if (_code < bound)
        {
            _range = bound;
            probs[index] += (ushort)((KBitModelTotal - probs[index]) >> KNumMoveBits);
            symbol = 0;
        }
        else
        {
            _range -= bound;
            _code -= bound;
            probs[index] -= (ushort)(probs[index] >> KNumMoveBits);
            symbol = 1;
        }
        Normalize();
        return symbol;
    }

    private uint DecodeDirectBits(int numBits)
    {
        uint result = 0;
        while (numBits-- > 0)
        {
            _range >>= 1;
            _code -= _range;
            uint t = 0u - (_code >> 31);
            _code += _range & t;
            result = (result << 1) + (t + 1);
            Normalize();
        }
        return result;
    }

    private int BitTree(ushort[] probs, int offset, int numBits)
    {
        int m = 1;
        for (int i = 0; i < numBits; i++) m = (m << 1) + (int)DecodeBit(probs, offset + m);
        return m - (1 << numBits);
    }

    private int BitTreeReverse(ushort[] probs, int offset, int numBits)
    {
        int m = 1, symbol = 0;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = DecodeBit(probs, offset + m);
            m = (m << 1) + (int)bit;
            symbol |= (int)bit << i;
        }
        return symbol;
    }

    // ---- LZMA state ----
    private const int KNumStates = 12;
    private const int KNumPosBitsMax = 4;
    private const int KNumLenToPosStates = 4;
    private const int KNumPosSlotBits = 6;
    private const int KNumAlignBits = 4;
    private const int KEndPosModelIndex = 14;
    private const int KNumFullDistances = 1 << (KEndPosModelIndex >> 1); // 128
    private const int KMatchMinLen = 2;

    private int _lc, _lp, _pb;

    private readonly ushort[] _isMatch = new ushort[KNumStates << KNumPosBitsMax];
    private readonly ushort[] _isRep = new ushort[KNumStates];
    private readonly ushort[] _isRepG0 = new ushort[KNumStates];
    private readonly ushort[] _isRepG1 = new ushort[KNumStates];
    private readonly ushort[] _isRepG2 = new ushort[KNumStates];
    private readonly ushort[] _isRep0Long = new ushort[KNumStates << KNumPosBitsMax];
    private readonly ushort[] _posSlot = new ushort[KNumLenToPosStates << KNumPosSlotBits];
    private readonly ushort[] _specPos = new ushort[KNumFullDistances - KEndPosModelIndex];
    private readonly ushort[] _align = new ushort[1 << KNumAlignBits];
    private ushort[] _literal = Array.Empty<ushort>();

    // length coders: [0]=choice, [1]=choice2, low 16*8, mid 16*8, high 256
    private readonly ushort[] _lenCoder = new ushort[2 + (16 << 3) + (16 << 3) + 256];
    private readonly ushort[] _repLenCoder = new ushort[2 + (16 << 3) + (16 << 3) + 256];

    private int _state;
    private uint _rep0, _rep1, _rep2, _rep3;

    private void SetProps(byte propByte)
    {
        if (propByte >= 9 * 5 * 5) throw new InvalidDataException("LZMA: invalid properties byte");
        _lc = propByte % 9;
        propByte /= 9;
        _lp = propByte % 5;
        _pb = propByte / 5;
        int litSize = 0x300 << (_lc + _lp);
        if (_literal.Length != litSize) _literal = new ushort[litSize];
    }

    private static void ResetProbs(ushort[] probs)
    {
        for (int i = 0; i < probs.Length; i++) probs[i] = KBitModelTotal >> 1;
    }

    private void ResetState()
    {
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        ResetProbs(_isMatch); ResetProbs(_isRep); ResetProbs(_isRepG0); ResetProbs(_isRepG1);
        ResetProbs(_isRepG2); ResetProbs(_isRep0Long); ResetProbs(_posSlot); ResetProbs(_specPos);
        ResetProbs(_align); ResetProbs(_literal); ResetProbs(_lenCoder); ResetProbs(_repLenCoder);
    }

    private int DecodeLen(ushort[] coder, int posState)
    {
        if (DecodeBit(coder, 0) == 0)
            return BitTree(coder, 2 + (posState << 3), 3);
        if (DecodeBit(coder, 1) == 0)
            return 8 + BitTree(coder, 2 + (16 << 3) + (posState << 3), 3);
        return 16 + BitTree(coder, 2 + (16 << 3) + (16 << 3), 8);
    }

    /// <summary>
    /// Decode one LZMA-coded run into <paramref name="output"/> until <paramref name="outLimit"/>.
    /// <paramref name="dictStart"/> is the window origin (bytes before it do not exist for context /
    /// match purposes — reset by an LZMA2 dictionary reset).
    /// </summary>
    private void DecodeRun(byte[] output, ref int outPos, int outLimit, int dictStart)
    {
        int pbMask = (1 << _pb) - 1, lpMask = (1 << _lp) - 1;
        while (outPos < outLimit)
        {
            int processed = outPos - dictStart;
            int posState = processed & pbMask;
            if (DecodeBit(_isMatch, (_state << KNumPosBitsMax) + posState) == 0)
            {
                byte prevByte = outPos > dictStart ? output[outPos - 1] : (byte)0;
                int litState = ((processed & lpMask) << _lc) + (prevByte >> (8 - _lc));
                int probsBase = 0x300 * litState;
                int symbol = 1;
                if (_state < 7)
                {
                    while (symbol < 0x100) symbol = (symbol << 1) + (int)DecodeBit(_literal, probsBase + symbol);
                }
                else
                {
                    int matchByte = output[outPos - (int)_rep0 - 1];
                    while (symbol < 0x100)
                    {
                        int matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        uint bit = DecodeBit(_literal, probsBase + ((1 + matchBit) << 8) + symbol);
                        symbol = (symbol << 1) + (int)bit;
                        if (matchBit != (int)bit)
                        {
                            while (symbol < 0x100) symbol = (symbol << 1) + (int)DecodeBit(_literal, probsBase + symbol);
                            break;
                        }
                    }
                }
                output[outPos++] = (byte)symbol;
                _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                continue;
            }

            int len;
            if (DecodeBit(_isRep, _state) != 0)
            {
                if (outPos == dictStart) throw new InvalidDataException("LZMA: rep match at window start");
                if (DecodeBit(_isRepG0, _state) == 0)
                {
                    if (DecodeBit(_isRep0Long, (_state << KNumPosBitsMax) + posState) == 0)
                    {
                        // short rep: copy 1 byte from rep0
                        _state = _state < 7 ? 9 : 11;
                        output[outPos] = output[outPos - (int)_rep0 - 1];
                        outPos++;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (DecodeBit(_isRepG1, _state) == 0)
                    {
                        distance = _rep1;
                    }
                    else
                    {
                        if (DecodeBit(_isRepG2, _state) == 0)
                        {
                            distance = _rep2;
                        }
                        else
                        {
                            distance = _rep3;
                            _rep3 = _rep2;
                        }
                        _rep2 = _rep1;
                    }
                    _rep1 = _rep0;
                    _rep0 = distance;
                }
                len = DecodeLen(_repLenCoder, posState) + KMatchMinLen;
                _state = _state < 7 ? 8 : 11;
            }
            else
            {
                _rep3 = _rep2; _rep2 = _rep1; _rep1 = _rep0;
                len = DecodeLen(_lenCoder, posState) + KMatchMinLen;
                _state = _state < 7 ? 7 : 10;
                int lenToPosState = len - KMatchMinLen < KNumLenToPosStates ? len - KMatchMinLen : KNumLenToPosStates - 1;
                int posSlot = BitTree(_posSlot, lenToPosState << KNumPosSlotBits, KNumPosSlotBits);
                if (posSlot < 4)
                {
                    _rep0 = (uint)posSlot;
                }
                else
                {
                    int numDirectBits = (posSlot >> 1) - 1;
                    _rep0 = (uint)((2 | (posSlot & 1)) << numDirectBits);
                    if (posSlot < KEndPosModelIndex)
                    {
                        _rep0 += (uint)BitTreeReverse(_specPos, (int)_rep0 - posSlot - 1, numDirectBits);
                    }
                    else
                    {
                        _rep0 += DecodeDirectBits(numDirectBits - KNumAlignBits) << KNumAlignBits;
                        _rep0 += (uint)BitTreeReverse(_align, 0, KNumAlignBits);
                        if (_rep0 == 0xFFFFFFFF)
                            throw new InvalidDataException("LZMA: unexpected end marker (sizes are known)");
                    }
                }
            }

            if (_rep0 >= (uint)(outPos - dictStart))
                throw new InvalidDataException("LZMA: match distance exceeds window");
            if (outPos + len > outLimit)
                throw new InvalidDataException("LZMA: match overruns declared unpacked size");
            int src = outPos - (int)_rep0 - 1;
            for (int i = 0; i < len; i++) output[outPos + i] = output[src + i];
            outPos += len;
        }
    }

    // ---- public entry points ----

    /// <summary>Decode a raw LZMA1 stream (7z coder 030101: 5-byte props = propByte + dictSize) into
    /// exactly <paramref name="output"/>.Length bytes.</summary>
    public static void DecodeLzma1(ReadOnlySpan<byte> props, ReadOnlySpan<byte> input, byte[] output)
    {
        if (props.Length < 1) throw new InvalidDataException("LZMA1: missing properties");
        var d = new Lzma { _in = input.ToArray(), _inPos = 0, _inEnd = input.Length };
        d.SetProps(props[0]);
        d.ResetState();
        d.RcInit();
        int outPos = 0;
        d.DecodeRun(output, ref outPos, output.Length, dictStart: 0);
    }

    /// <summary>Decode an LZMA2 stream (7z coder 21) into exactly <paramref name="output"/>.Length bytes.</summary>
    public static void DecodeLzma2(ReadOnlySpan<byte> input, byte[] output)
    {
        var d = new Lzma { _in = input.ToArray(), _inPos = 0, _inEnd = input.Length };
        int outPos = 0, dictStart = 0;
        bool propsSet = false, stateInitialized = false;
        while (true)
        {
            if (d._inPos >= d._inEnd) throw new InvalidDataException("LZMA2: missing end-of-stream control byte");
            byte control = d._in[d._inPos++];
            if (control == 0) break;

            if (control < 0x80)
            {
                if (control > 2) throw new InvalidDataException($"LZMA2: bad control byte 0x{control:X2}");
                int size = ((d.NextIn() << 8) | d.NextIn()) + 1;
                if (control == 1) dictStart = outPos; // dict reset
                if (outPos + size > output.Length || d._inPos + size > d._inEnd)
                    throw new InvalidDataException("LZMA2: uncompressed chunk overrun");
                Array.Copy(d._in, d._inPos, output, outPos, size);
                d._inPos += size;
                outPos += size;
                stateInitialized = false; // next LZMA chunk must reset state
            }
            else
            {
                int unpackSize = (((control & 0x1F) << 16) | (d.NextIn() << 8) | d.NextIn()) + 1;
                int compSize = ((d.NextIn() << 8) | d.NextIn()) + 1;
                int mode = (control >> 5) & 3;
                if (mode >= 2)
                {
                    d.SetProps(d.NextIn());
                    propsSet = true;
                }
                if (!propsSet) throw new InvalidDataException("LZMA2: chunk before properties");
                if (mode == 3) dictStart = outPos;
                if (mode >= 1)
                {
                    d.ResetState();
                    stateInitialized = true;
                }
                if (!stateInitialized)
                    throw new InvalidDataException("LZMA2: continuation chunk without initialized state");

                int chunkInEnd = d._inPos + compSize;
                if (chunkInEnd > d._inEnd || outPos + unpackSize > output.Length)
                    throw new InvalidDataException("LZMA2: chunk overruns stream bounds");
                int savedEnd = d._inEnd;
                d._inEnd = chunkInEnd; // the range coder must not read past this chunk
                d.RcInit();
                d.DecodeRun(output, ref outPos, outPos + unpackSize, dictStart);
                if (d._inPos != chunkInEnd)
                    throw new InvalidDataException("LZMA2: chunk did not consume its declared packed size");
                d._inEnd = savedEnd;
            }
        }
        if (outPos != output.Length)
            throw new InvalidDataException($"LZMA2: decoded 0x{outPos:X} of expected 0x{output.Length:X} bytes");
    }
}
