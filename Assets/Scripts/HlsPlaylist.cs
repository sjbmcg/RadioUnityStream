using System;
using System.Collections.Generic;

public class HlsVariant
{
    public int Bandwidth;
    public string Url;
}

public class HlsSegment
{
    public string Url;
    public float Duration;
    public int Sequence;
}

public class HlsMediaPlaylist
{
    public List<HlsSegment> Segments;
    public int TargetDuration;
    public bool IsLive;
    public string InitUrl;  // from #EXT-X-MAP:URI="..."
}

public static class HlsPlaylist
{
    private const string TagStreamInf      = "#EXT-X-STREAM-INF:";
    private const string TagTargetDuration = "#EXT-X-TARGETDURATION:";
    private const string TagMediaSequence  = "#EXT-X-MEDIA-SEQUENCE:";
    private const string TagEndList        = "#EXT-X-ENDLIST";
    private const string TagExtInf         = "#EXTINF:";
    private const string TagMap            = "#EXT-X-MAP:";

    public static List<HlsVariant> ParseMaster(string m3u8, string baseUrl)
    {
        var variants = new List<HlsVariant>();
        var lines = SplitLines(m3u8);

        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith(TagStreamInf, StringComparison.Ordinal))
                continue;

            int bandwidth = 0;
            string attrs = line.Substring(TagStreamInf.Length);
            foreach (string attr in attrs.Split(','))
            {
                if (attr.StartsWith("BANDWIDTH=", StringComparison.Ordinal) &&
                    int.TryParse(attr.Substring("BANDWIDTH=".Length), out int bw))
                    bandwidth = bw;
            }

            string uri = lines[i + 1].Trim();
            if (!string.IsNullOrEmpty(uri) && !uri.StartsWith("#", StringComparison.Ordinal))
            {
                variants.Add(new HlsVariant { Bandwidth = bandwidth, Url = Resolve(uri, baseUrl) });
                i++;
            }
        }

        return variants;
    }

    public static HlsMediaPlaylist ParseMedia(string m3u8, string baseUrl)
    {
        var segments = new List<HlsSegment>();
        var lines = SplitLines(m3u8);

        int targetDuration = 0;
        int mediaSequence = 0;
        bool isLive = true;
        float nextDuration = 0f;
        int seq = 0;
        string initUrl = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith(TagMap, StringComparison.Ordinal))
            {
                // #EXT-X-MAP:URI="init.mp4"
                int uriStart = line.IndexOf("URI=\"", StringComparison.Ordinal);
                if (uriStart >= 0)
                {
                    uriStart += 5;
                    int uriEnd = line.IndexOf('"', uriStart);
                    if (uriEnd > uriStart)
                        initUrl = Resolve(line.Substring(uriStart, uriEnd - uriStart), baseUrl);
                }
            }
            else if (line.StartsWith(TagTargetDuration, StringComparison.Ordinal))
            {
                int.TryParse(line.Substring(TagTargetDuration.Length), out targetDuration);
            }
            else if (line.StartsWith(TagMediaSequence, StringComparison.Ordinal))
            {
                int.TryParse(line.Substring(TagMediaSequence.Length), out mediaSequence);
                seq = mediaSequence;
            }
            else if (line.Equals(TagEndList, StringComparison.Ordinal))
            {
                isLive = false;
            }
            else if (line.StartsWith(TagExtInf, StringComparison.Ordinal))
            {
                string val = line.Substring(TagExtInf.Length);
                int commaIdx = val.IndexOf(',');
                if (commaIdx >= 0) val = val.Substring(0, commaIdx);
                float.TryParse(val, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out nextDuration);
            }
            else if (!string.IsNullOrEmpty(line) && !line.StartsWith("#", StringComparison.Ordinal))
            {
                segments.Add(new HlsSegment
                {
                    Url = Resolve(line, baseUrl),
                    Duration = nextDuration,
                    Sequence = seq++
                });
                nextDuration = 0f;
            }
        }

        return new HlsMediaPlaylist
        {
            Segments = segments,
            TargetDuration = targetDuration,
            IsLive = isLive,
            InitUrl = initUrl
        };
    }

    private static string Resolve(string uri, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl) || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return uri;

        if (uri.StartsWith("/", StringComparison.Ordinal))
        {
            var root = new Uri(baseUrl);
            return root.GetLeftPart(UriPartial.Authority) + uri;
        }

        int lastSlash = baseUrl.LastIndexOf('/');
        return lastSlash >= 0 ? baseUrl.Substring(0, lastSlash + 1) + uri : uri;
    }

    private static string[] SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
}
