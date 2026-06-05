using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PyConnectKiosk;

public static class PyConnectService
{
    private static int _pyKeyIndex = 0;

    private static string NextPyKey()
    {
        var pool = PyConnectSettings.PyKeyPool;
        if (pool.Count == 0)
            throw new InvalidOperationException(
                "PyKeyPool is empty — add at least one PyKey in AppConfig.cs");
        int idx = Interlocked.Increment(ref _pyKeyIndex) % pool.Count;
        return pool[idx];
    }

    /// <summary>
    /// For each rack, generate <paramref name="codesPerRack"/> OTC codes sequentially.
    /// </summary>
    public static async Task<List<OtcResult>> GenerateMultipleAsync(
        List<RackItem> racks, string ticket, int codesPerRack = 1)
    {
        var results = new List<OtcResult>();
        int reqId = 1;

        foreach (var rack in racks)
        {
            var codes = new List<string>();
            bool anyError = false;
            string errCode = "0x00";

            for (int n = 0; n < codesPerRack; n++)
            {
                try
                {
                    string code = await RequestOtcAsync(reqId++, ticket, rack.LockId);
                    codes.Add(code);
                }
                catch (Exception ex)
                {
                    anyError = true;
                    errCode = "ERR";
                    var parts = ex.Message.Split(' ');
                    var token = Array.Find(parts, p => p.StartsWith("0x"));
                    if (token != null) errCode = token.TrimEnd(':');
                    codes.Add(ex.Message);
                    // TEMPORARY — show full error
                    System.Windows.MessageBox.Show(ex.Message, "PyConnect Error"); //Remove 
                    break;   // stop requesting more codes for this rack on error
                }
            }

            results.Add(new OtcResult(
                Rack: rack,
                Success: !anyError,
                Codes: codes,
                ErrorCode: errCode
            ));
        }

        return results;
    }

    /// <summary>
    /// Generate OTPs for explicit (rack, pyKey) pairs.
    /// Used by the "floating key" draw flow where the dispensed key is decided
    /// at draw-time from whatever physical key is currently in the cabinet,
    /// not from rack.LockId. The OtcResult still references the rack the user
    /// selected (for receipts &amp; UI), but the OTP is bound to the supplied pyKey.
    /// </summary>
    public static async Task<List<OtcResult>> GenerateForAssignmentsAsync(
        List<(RackItem rack, string pyKey)> assignments, string ticket, int codesPerRack = 1)
    {
        var results = new List<OtcResult>();
        int reqId = 1;

        foreach (var (rack, pyKey) in assignments)
        {
            var codes = new List<string>();
            bool anyError = false;
            string errCode = "0x00";

            for (int n = 0; n < codesPerRack; n++)
            {
                try
                {
                    string code = await RequestOtcAsync(reqId++, ticket, pyKey);
                    codes.Add(code);
                }
                catch (Exception ex)
                {
                    anyError = true;
                    errCode = "ERR";
                    var parts = ex.Message.Split(' ');
                    var token = Array.Find(parts, p => p.StartsWith("0x"));
                    if (token != null) errCode = token.TrimEnd(':');
                    codes.Add(ex.Message);
                    break;
                }
            }

            results.Add(new OtcResult(
                Rack: rack,
                Success: !anyError,
                Codes: codes,
                ErrorCode: errCode
            ));
        }

        return results;
    }

    private static async Task<string> RequestOtcAsync(
        int reqId, string ticket, string lockId)
    {
        string pykey = NextPyKey();
        string reqTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        string contents = $"{reqId}{reqTime}{ticket}{pykey}{lockId}";
        string checksum = Sha256Hex(contents + PyConnectSettings.ChecksumPassword);

        string xml = BuildXml(reqId, reqTime, ticket, pykey, lockId, checksum);
        string response = await SendAsync(xml);
        return ParseOtc(response);
    }

    private static string BuildXml(
        int reqId, string reqTime, string ticket,
        string pykey, string lockId, string checksum)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<lywXML version=""3.0"">
  <ocReqList>
    <ocRequest checksum=""{checksum}"">
      <reqID>{reqId}</reqID>
      <reqDateAdded>{reqTime}</reqDateAdded>
      <reqTicket>{System.Security.SecurityElement.Escape(ticket)}</reqTicket>
      <reqIdentPK>{System.Security.SecurityElement.Escape(pykey)}</reqIdentPK>
      <reqIdentLO>{System.Security.SecurityElement.Escape(lockId)}</reqIdentLO>
    </ocRequest>
  </ocReqList>
</lywXML>
";
    }

    private static async Task<string> SendAsync(string xml)
    {
        DebugConsole.Show("PyConnect", $"XML being sent:\n{xml}");

        byte[] payload = Encoding.UTF8.GetBytes(xml + "/eof");

        using var client = new TcpClient();
        client.ReceiveTimeout = PyConnectSettings.TimeoutMs;
        client.SendTimeout = PyConnectSettings.TimeoutMs;

        await client.ConnectAsync(PyConnectSettings.Host, PyConnectSettings.Port);

        using var stream = client.GetStream();
        await stream.WriteAsync(payload, 0, payload.Length);
        await stream.FlushAsync();

        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        var cts = new CancellationTokenSource(
                      TimeSpan.FromMilliseconds(PyConnectSettings.TimeoutMs));

        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token); }
            catch (OperationCanceledException) { break; }
            if (read <= 0) break;
            ms.Write(buffer, 0, read);
            if (Encoding.UTF8.GetString(ms.ToArray()).Contains("</lywXML>")) break;
        }

        string response = Encoding.UTF8.GetString(ms.ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(response))
            throw new Exception("Empty response from PYCONNECT.");

        return response;
    }

    private static string ParseOtc(string response)
    {
        var doc = XDocument.Parse(response);
        var node = doc.Descendants("ocResponse").FirstOrDefault()
                   ?? throw new Exception("No <ocResponse> in PYCONNECT response.");

        string errCode = node.Element("reqErrorCode")?.Value?.Trim() ?? "";
        string errMsg = node.Element("reqErrors")?.Value?.Trim() ?? "";

        if (!string.IsNullOrEmpty(errCode) && errCode != "0x00")
        {
            string meaning = errCode switch
            {
                "0x01" => "XML schema invalid",
                "0x02" => "Checksum check failed",
                "0x03" => "No lock specified",
                "0x04" => "No pyKey specified",
                "0x05" => "No ticket specified",
                "0x06" => "Lock not in database",
                "0x07" => "pyKey not in database",
                "0x08" => "Error during OTC creation",
                "0x09" => "Blacklist query failed",
                "0x0A" => "pyKey not authorized on lock",
                "0x0B" => "pyKey not assigned to user",
                "0x0C" => "History entry write failed",
                _ => "Unknown error"
            };
            throw new Exception($"PYCONNECT {errCode}: {meaning} | {errMsg}");
        }

        string? code = node.Element("reqOC")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(code))
            throw new Exception("Missing <reqOC> in PYCONNECT response.");

        return code;
    }

    private static string Sha256Hex(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
