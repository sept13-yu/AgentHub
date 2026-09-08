using System.Buffers.Binary;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Cursor Grok Bot（Sand）ConnectRPC：GetSandAccessStatus + GetSandUsageStatus。
/// Protobuf 字段号对齐 TokenTracker cursor-config.js（非官方，可能随时变）。
/// </summary>
internal static class CursorSand
{
    private const string AccessUrl =
        "https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandAccessStatus";
    private const string UsageUrl =
        "https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus";
    private const int MaxResponseBytes = 64 * 1024;

    internal sealed record Result(bool Granted, double? UsagePercent, string? NextResetAt, string? Error);

    public static async Task<Result> TryFetchAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        try
        {
            var accessRaw = await PostProtoAsync(http, AccessUrl, accessToken, ct).ConfigureAwait(false);
            if (accessRaw.Error is not null)
                return new Result(false, null, null, accessRaw.Error);

            var access = DecodeAccess(accessRaw.Bytes!);
            if (!access.Granted)
                return new Result(false, null, null, null);

            var usageRaw = await PostProtoAsync(http, UsageUrl, accessToken, ct).ConfigureAwait(false);
            if (usageRaw.Error is not null)
                return new Result(true, null, null, usageRaw.Error);

            var usage = DecodeUsage(usageRaw.Bytes!);
            return new Result(true, usage.UsagePercent, usage.NextResetAt, null);
        }
        catch (Exception ex)
        {
            return new Result(false, null, null, ex.Message);
        }
    }

    private sealed record ProtoResponse(byte[]? Bytes, string? Error);

    private static async Task<ProtoResponse> PostProtoAsync(
        HttpClient http, string url, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code is 401 or 403)
            return new ProtoResponse(null, "会话过期（401/403）：打开 Cursor 重新登录");
        if (!resp.IsSuccessStatusCode)
            return new ProtoResponse(null, $"Sand 接口返回 HTTP {code}");

        var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!ctype.StartsWith("application/proto", StringComparison.OrdinalIgnoreCase))
            return new ProtoResponse(null, "Sand 接口返回非 protobuf 响应");

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > MaxResponseBytes)
            return new ProtoResponse(null, "Sand 接口响应过大");
        return new ProtoResponse(bytes, null);
    }

    private sealed record AccessDecoded(bool Granted);

    private static AccessDecoded DecodeAccess(ReadOnlySpan<byte> input)
    {
        int state = 0;
        foreach (var field in ReadFields(input))
        {
            if (field.FieldNumber == 1 && field.WireType == 0)
                state = (int)field.Varint;
        }
        return new AccessDecoded(state == 1);
    }

    private sealed record UsageDecoded(double? UsagePercent, string? NextResetAt);

    private static UsageDecoded DecodeUsage(ReadOnlySpan<byte> input)
    {
        double? usagePercent = null;
        string? nextResetAt = null;
        foreach (var field in ReadFields(input))
        {
            if (field.FieldNumber == 2 && field.WireType == 2 && field.Bytes is not null)
                nextResetAt = TimestampToIso(field.Bytes);
            else if (field.FieldNumber == 3 && field.WireType == 1 && field.Fixed64 is ulong bits)
            {
                var d = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                if (double.IsFinite(d)) usagePercent = d;
            }
        }
        return new UsageDecoded(usagePercent, nextResetAt);
    }

    private static string? TimestampToIso(ReadOnlySpan<byte> input)
    {
        long seconds = 0;
        int nanos = 0;
        foreach (var field in ReadFields(input))
        {
            if (field.FieldNumber == 1 && field.WireType == 0)
                seconds = unchecked((long)field.Varint);
            else if (field.FieldNumber == 2 && field.WireType == 0)
                nanos = (int)field.Varint;
        }
        try
        {
            var dto = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
            return dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly struct ProtoField
    {
        public int FieldNumber { get; init; }
        public int WireType { get; init; }
        public ulong Varint { get; init; }
        public ulong? Fixed64 { get; init; }
        public byte[]? Bytes { get; init; }
    }

    private static List<ProtoField> ReadFields(ReadOnlySpan<byte> buffer)
    {
        var fields = new List<ProtoField>();
        var offset = 0;
        while (offset < buffer.Length)
        {
            if (!TryReadVarint(buffer, ref offset, out var tag))
                break;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (fieldNumber <= 0) break;

            switch (wireType)
            {
                case 0: // varint
                {
                    if (!TryReadVarint(buffer, ref offset, out var value)) return fields;
                    fields.Add(new ProtoField { FieldNumber = fieldNumber, WireType = 0, Varint = value });
                    break;
                }
                case 1: // 64-bit
                {
                    if (offset + 8 > buffer.Length) return fields;
                    var bits = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset, 8));
                    offset += 8;
                    fields.Add(new ProtoField { FieldNumber = fieldNumber, WireType = 1, Fixed64 = bits });
                    break;
                }
                case 2: // length-delimited
                {
                    if (!TryReadVarint(buffer, ref offset, out var lenU)) return fields;
                    var len = (int)lenU;
                    if (len < 0 || offset + len > buffer.Length) return fields;
                    var slice = buffer.Slice(offset, len).ToArray();
                    offset += len;
                    fields.Add(new ProtoField { FieldNumber = fieldNumber, WireType = 2, Bytes = slice });
                    break;
                }
                case 5: // 32-bit
                {
                    if (offset + 4 > buffer.Length) return fields;
                    offset += 4;
                    break;
                }
                default:
                    return fields;
            }
        }
        return fields;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> buffer, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (offset < buffer.Length && shift < 64)
        {
            var b = buffer[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
