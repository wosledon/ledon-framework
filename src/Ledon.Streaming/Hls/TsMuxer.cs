using System.Buffers;
using System.Buffers.Binary;

namespace Ledon.Streaming.Hls;

/// <summary>
/// MPEG-TS 复用器：把 FLV 音视频 Tag 写成 TS 包（PAT + PMT + PES）。
/// HLS 播放的底层容器；支持 H.264(AVC) 视频 + AAC 音频（直播的主流组合）。
/// </summary>
public sealed class TsMuxer
{
    /// <summary>TS 包大小（188 字节，DVB/MPEG-TS 标准）。</summary>
    public const int TsPacketSize = 188;

    private const ushort PmtPid = 0x1000;
    private const ushort VideoPid = 0x0100;
    private const ushort AudioPid = 0x0101;

    private byte _continuity;
    private bool _patWritten;
    private bool _pmtWritten;
    private long _pcr;

    /// <summary>当前 PCR（90kHz 时钟）。</summary>
    public long Pcr => Interlocked.Read(ref _pcr);

    /// <summary>
    /// 把一个 FLV Tag 写成 TS 包（追加到 <paramref name="writer"/>）。
    /// 首个 Tag 前会自动写 PAT + PMT。
    /// </summary>
    public void Write(IBufferWriter<byte> writer, Media.FlvTag tag)
    {
        if (!_patWritten)
        {
            WriteSectionPacket(writer, 0x0000, BuildPat());
            _patWritten = true;
        }

        if (!_pmtWritten)
        {
            WriteSectionPacket(writer, PmtPid, BuildPmt());
            _pmtWritten = true;
        }

        switch (tag.TagType)
        {
            case Media.FlvTagType.Video:
                WritePes(writer, VideoPid, streamId: 0xE0, isVideo: true, isKeyFrame: IsKeyFrame(tag), tag);
                break;

            case Media.FlvTagType.Audio:
                WritePes(writer, AudioPid, streamId: 0xC0, isVideo: false, isKeyFrame: false, tag);
                break;
        }

        Interlocked.Exchange(ref _pcr, tag.TimestampMs * 90L);
    }

    private static bool IsKeyFrame(Media.FlvTag tag) =>
        tag.Data.Length > 0 && (tag.Data[0] >> 4) is 1 or 4;

    // ---------- PAT / PMT ----------

    private static byte[] BuildPat()
    {
        var section = new byte[16];
        section[0] = 0x00;                                                     // table_id: PAT
        WriteUInt16(section, 1, (ushort)(0xB000 | 13));                        // section_syntax_indicator + length
        WriteUInt16(section, 3, 1);                                            // transport_stream_id
        section[5] = 0xC1;                                                     // 版本 0 + current_next
        section[6] = 0x00;                                                     // section_number
        section[7] = 0x00;                                                     // last_section_number
        WriteUInt16(section, 8, 1);                                            // program_number
        WriteUInt16(section, 10, (ushort)(0xE000 | PmtPid));                   // reserved + PMT PID
        WriteUInt32(section, 12, Crc32(section, 0, 12));

        return section;
    }

    private static byte[] BuildPmt()
    {
        // 头部 12 字节 + 视频流 5 字节 + 音频流 5 字节 + CRC 4 字节 = 26
        var section = new byte[26];
        section[0] = 0x02;                                                     // table_id: PMT
        WriteUInt16(section, 1, (ushort)(0xB000 | 18));                        // length = 18（13 + 5 + 5 - ... 按标准算法）
        WriteUInt16(section, 3, 1);                                            // program_number
        section[5] = 0xC1;
        section[6] = 0x00;
        section[7] = 0x00;
        WriteUInt16(section, 8, (ushort)(0xE000 | VideoPid));                  // PCR_PID = 视频 PID
        WriteUInt16(section, 10, 0xF000);                                      // program_info_length = 0

        // 视频流：H.264
        section[12] = 0x1B;                                                    // stream_type: H.264
        WriteUInt16(section, 13, (ushort)(0xE000 | VideoPid));
        WriteUInt16(section, 15, 0xF000);                                      // ES_info_length = 0

        // 音频流：AAC
        section[17] = 0x0F;                                                    // stream_type: AAC ADTS
        WriteUInt16(section, 18, (ushort)(0xE000 | AudioPid));
        WriteUInt16(section, 20, 0xF000);

        WriteUInt32(section, 22, Crc32(section, 0, 22));

        return section;
    }

    // ---------- PES ----------

    private void WritePes(
        IBufferWriter<byte> writer,
        ushort pid,
        byte streamId,
        bool isVideo,
        bool isKeyFrame,
        Media.FlvTag tag)
    {
        // 剥掉 FLV Tag 头，留下裸 ES
        var es = isVideo
            ? (tag.Data.Length > 5 ? tag.Data[5..] : [])     // 1 字节帧头 + 4 字节 AVC 封装头
            : (tag.Data.Length > 1 ? tag.Data[1..] : []);   // 1 字节音频头

        var pts = tag.TimestampMs * 90L;

        // PES 头：start_code(3) + stream_id(1) + packet_length(2) + flags(2) + pts(5)
        var pes = new byte[14 + es.Length];
        pes[0] = 0x00;
        pes[1] = 0x00;
        pes[2] = 0x01;
        pes[3] = streamId;
        WriteUInt16(pes, 4, (ushort)(3 + 5 + es.Length));
        pes[6] = 0x80;                                          // '10' + 仅 PTS
        pes[7] = 0x80;                                          // PTS_DTS_flags = '10'
        pes[8] = 5;                                             // PES_header_data_length
        WritePts(pes, 9, pts);
        es.CopyTo(pes, 14);

        WritePayload(writer, pid, pes, payloadUnitStart: true);
    }

    private static void WritePts(byte[] buffer, int offset, long pts)
    {
        buffer[offset] = (byte)((((pts >> 30) & 0x07) << 1) | 0x21);   // '0010' + PTS[32..30] + marker
        buffer[offset + 1] = (byte)(pts >> 22);
        buffer[offset + 2] = (byte)((((pts >> 15) & 0x7F) << 1) | 1);
        buffer[offset + 3] = (byte)(pts >> 7);
        buffer[offset + 4] = (byte)(((pts & 0x7F) << 1) | 1);
    }

    // ---------- TS 包 ----------

    /// <summary>把一段负载切成 188 字节 TS 包（首包带 PUSI，负载不满用 adaptation 填充）。</summary>
    private void WritePayload(IBufferWriter<byte> writer, ushort pid, ReadOnlySpan<byte> payload, bool payloadUnitStart)
    {
        var offset = 0;
        var first = true;

        while (offset < payload.Length || first)
        {
            var remaining = payload.Length - offset;
            var packet = writer.GetSpan(TsPacketSize);
            packet[..TsPacketSize].Clear();

            packet[0] = 0x47;   // sync byte
            WriteUInt16(packet, 1, (ushort)(((payloadUnitStart && first) ? 0x4000 : 0) | (pid & 0x1FFF)));
            packet[3] = (byte)(0x10 | (_continuity++ & 0x0F));   // payload only + 连续性计数器

            var payloadOffset = 4;

            if (first)
            {
                // 首包负载不满 184 时，用 adaptation field 补足，避免播放器按错边界
                var adaptationLength = 184 - remaining - 1;
                if (adaptationLength >= 0)
                {
                    packet[payloadOffset] = (byte)adaptationLength;
                    payloadOffset += 1 + adaptationLength;
                }
            }

            var chunk = Math.Min(TsPacketSize - payloadOffset, remaining);
            payload.Slice(offset, chunk).CopyTo(packet.Slice(payloadOffset));
            offset += chunk;

            writer.Advance(TsPacketSize);

            first = false;

            if (chunk == 0)
                break;
        }
    }

    private void WriteSectionPacket(IBufferWriter<byte> writer, ushort pid, byte[] section) =>
        WritePayload(writer, pid, section, payloadUnitStart: true);

    // ---------- 工具 ----------

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), value);

    private static void WriteUInt16(Span<byte> buffer, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);

    /// <summary>MPEG-2 CRC-32（多项式 0x04C11DB7，与 TS 标准一致）。</summary>
    private static uint Crc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;

        for (var i = offset; i < offset + length; i++)
        {
            crc ^= (uint)data[i] << 24;

            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        return crc;
    }
}
