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
    public int MediaSequence;
}

public static class HlsPlaylist
{
    public static List<HlsVariant> ParseMaster(string m3u8, string baseUrl)
    {
        var variants = new List<HlsVariant>();
        var lines = SplitLines(m3u8);

        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                continue;

            int bandwidth = 0;
            string attrs = line.Substring("#EXT-X-STREAM-INF:".Length);
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

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                int.TryParse(line.Substring("#EXT-X-TARGETDURATION:".Length), out targetDuration);
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                int.TryParse(line.Substring("#EXT-X-MEDIA-SEQUENCE:".Length), out mediaSequence);
                seq = mediaSequence;
            }
            else if (line.Equals("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                isLive = false;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                string val = line.Substring("#EXTINF:".Length);
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
            MediaSequence = mediaSequence
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
