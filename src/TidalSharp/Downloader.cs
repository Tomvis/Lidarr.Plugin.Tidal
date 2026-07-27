using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using Org.BouncyCastle.Tsp;
using System.Globalization;
using TidalSharp.Data;
using TidalSharp.Downloading;
using TidalSharp.Exceptions;
using TidalSharp.Metadata;

namespace TidalSharp;

public class Downloader
{
    internal Downloader(IHttpClient client, API api, Session session)
    {
        _client = client;
        _api = api;
        _session = session;
    }

    private readonly IHttpClient _client;
    private readonly API _api;
    private readonly Session _session;

    public async Task<DownloadData<Stream>> GetRawTrackStream(string trackId, AudioQuality quality, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var (stream, manifest) = await GetTrackStream(trackId, quality, onChunkDownloaded, token);
        return new(stream, manifest.FileExtension);
    }

    public async Task<DownloadData<byte[]>> GetRawTrackBytes(string trackId, AudioQuality quality, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var (stream, manifest) = await GetTrackStream(trackId, quality, onChunkDownloaded, token);
        var data = new DownloadData<byte[]>(stream.ToArray(), manifest.FileExtension);

        await stream.DisposeAsync();

        return data;
    }

    public async Task WriteRawTrackToFile(string trackId, AudioQuality quality, string trackPath, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var (stream, manifest) = await GetTrackStream(trackId, quality, onChunkDownloaded, token);
        using FileStream fileStream = File.Open(trackPath, FileMode.Create);

        await stream.CopyToAsync(fileStream, token);
        await stream.DisposeAsync();
    }

    public async Task<string> GetExtensionForTrack(string trackId, AudioQuality quality, CancellationToken token = default)
    {
        var trackStreamData = await GetTrackStreamData(trackId, quality, token);
        var streamManifest = new StreamManifest(trackStreamData);
        return streamManifest.FileExtension;
    }

    public async Task<int> GetChunksInTrack(string trackId, AudioQuality quality, CancellationToken token = default)
    {
        var trackStreamData = await GetTrackStreamData(trackId, quality, token);
        var streamManifest = new StreamManifest(trackStreamData);
        return streamManifest.Urls.Length;
    }

    public async Task<byte[]> GetImageBytes(string id, MediaResolution resolution, CancellationToken token = default)
    {
        var request = _client
            .BuildRequest(Globals.IMAGE_URL_BASE)
            .Resource(Globals.GetImageResoursePath(id, resolution));
        var response = await _client.ProcessRequestAsync(request);

        // requests are built with SuppressHttpError, so every failure has to be caught here;
        // otherwise an error body (404/403 for a resolution tidal never published) would be
        // handed back to callers and embedded as if it were the cover.
        if (response.HasHttpError)
        {
            throw new UnavailableMediaException($"The image with {id} with resolution {resolution} is unavailable (HTTP {(int)response.StatusCode}).");
        }

        return response.ResponseData;
    }

    public async Task ApplyMetadataToTrackStream(string trackId, DownloadData<Stream> trackData, MediaResolution coverResolution = MediaResolution.s1280, string lyrics = "", CancellationToken token = default)
    {
        byte[] magicBuffer = new byte[4];
        await trackData.Data.ReadAsync(magicBuffer.AsMemory(0, 4), token);

        trackData.Data.Seek(0, SeekOrigin.Begin);

        StreamAbstraction abstraction = new("track" + trackData.FileExtension, trackData.Data);
        using TagLib.File file = TagLib.File.Create(abstraction);
        await ApplyMetadataToTagLibFile(file, trackId, coverResolution, lyrics, token);

        trackData.Data.Seek(0, SeekOrigin.Begin);
    }

    public async Task ApplyMetadataToTrackBytes(string trackId, DownloadData<byte[]> trackData, MediaResolution coverResolution = MediaResolution.s1280, string lyrics = "", CancellationToken token = default)
    {
        FileBytesAbstraction abstraction = new("track" + trackData.FileExtension, trackData.Data);
        using TagLib.File file = TagLib.File.Create(abstraction);
        await ApplyMetadataToTagLibFile(file, trackId, coverResolution, lyrics, token);

        byte[] finalData = abstraction.MemoryStream.ToArray();
        await abstraction.MemoryStream.DisposeAsync();
        trackData.Data = finalData;
    }

    public async Task ApplyMetadataToFile(string trackId, string trackPath, MediaResolution coverResolution = MediaResolution.s1280, string lyrics = "", CancellationToken token = default)
    {
        using TagLib.File file = TagLib.File.Create(trackPath);
        await ApplyMetadataToTagLibFile(file, trackId, coverResolution, lyrics, token);
    }

    public async Task<(string? plainLyrics, string? syncLyrics)?> FetchLyricsFromTidal(string trackId, CancellationToken token = default)
    {
        var lyrics = await _api.GetTrackLyrics(trackId, token);
        if (lyrics == null)
            return null;

        return (lyrics.Lyrics, lyrics.Subtitles);
    }

    public async Task<(string? plainLyrics, string? syncLyrics)?> FetchLyricsFromLRCLIB(string instance, string trackName, string artistName, string albumName, int duration, CancellationToken token = default)
    {
        var requestResource = $"/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackName)}&album_name={Uri.EscapeDataString(albumName)}&duration={duration}";
        var request = _client
            .BuildRequest($"https://{instance}")
            .Resource(requestResource);
        var response = await _client.ProcessRequestAsync(request);

        if (!response.HasHttpError)
        {
            var content = response.Content;
            var json = JObject.Parse(content);
            return (json["plainLyrics"]?.ToString(), json["syncedLyrics"]?.ToString());
        }

        return null;
    }

    // TODO: video downloading, this is less important as this is mainly for lidarr

    private async Task ApplyMetadataToTagLibFile(TagLib.File track, string trackId, MediaResolution coverResolution = MediaResolution.s1280, string lyrics = "", CancellationToken token = default)
    {
        JToken trackData = await _api.GetTrack(trackId, token);
        string albumId = trackData["album"]!["id"]!.ToString();
        JToken albumPage = await _api.GetAlbum(albumId, token);

        byte[]? albumArt = await GetCoverBytes(trackData["album"]!["cover"]!.ToString(), coverResolution, token);

        track.Tag.Title = API.CompleteTitleFromPage(trackData);
        track.Tag.Album = API.CompleteTitleFromPage(albumPage);
        track.Tag.Performers = trackData["artists"]!.Select(a => a["name"]!.ToString()).ToArray();
        track.Tag.AlbumArtists = albumPage["artists"]!.Select(a => a["name"]!.ToString()).ToArray();
        string? rawReleaseDate = albumPage["releaseDate"]?.ToString() ?? albumPage["streamStartDate"]?.ToString();
        DateTime releaseDate = !string.IsNullOrEmpty(rawReleaseDate) ? DateTime.Parse(rawReleaseDate, CultureInfo.InvariantCulture) : DateTime.MinValue;
        track.Tag.Year = (uint)releaseDate.Year;
        track.Tag.Track = uint.Parse(trackData["trackNumber"]!.ToString());
        track.Tag.TrackCount = uint.Parse(albumPage["numberOfTracks"]!.ToString());
        track.Tag.Disc = uint.Parse(trackData["volumeNumber"]!.ToString());
        track.Tag.DiscCount = uint.Parse(albumPage["numberOfVolumes"]!.ToString());
        if (albumArt != null)
            track.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(albumArt))];
        track.Tag.Lyrics = lyrics;

        track.Save();
    }

    // tidal does not publish the largest cover size for every release, so ask for the requested
    // resolution first and step down to s640 when it is missing. the s640 floor is a deliberate
    // compatibility choice, not a quality guarantee: s640 is what this plugin embedded before the
    // bump, so stopping there leaves every release no worse off than it was. it is knowingly below
    // what downstream art taggers accept (artextra's front_min_edge is 1000), so a release with no
    // s1280 asset still ends up with sub-threshold art embedded - that is the accepted trade.
    private async Task<byte[]?> GetCoverBytes(string coverId, MediaResolution coverResolution, CancellationToken token = default)
    {
        var albumArt = await TryGetCoverBytes(coverId, coverResolution, token);

        if (albumArt == null && coverResolution > MediaResolution.s640)
            albumArt = await TryGetCoverBytes(coverId, MediaResolution.s640, token);

        return albumArt;
    }

    // null means "do not embed this". an ok status is not proof of an image: an empty or truncated
    // body, or an error page served as 200, becomes a picture with no mime type, and taggers that
    // only fill in art for files that have none would never replace it.
    private async Task<byte[]?> TryGetCoverBytes(string coverId, MediaResolution resolution, CancellationToken token = default)
    {
        byte[]? data = null;
        try { data = await GetImageBytes(coverId, resolution, token); } catch (UnavailableMediaException) { }

        return IsUsableCover(data) ? data : null;
    }

    // this path never asks for less than s640 and the cdn only serves jpeg here, so anything under
    // a kilobyte or without an image header is a truncated or non-image body.
    private static bool IsUsableCover(byte[]? data)
        => data != null && data.Length >= 1024 &&
           ((data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) ||                       // jpeg
            (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47));     // png

    // TODO: implement method to extract flacs from the m4a containers
    // tidal-dl-ng uses ffmpeg but thats not ideal in this case
    private async Task<(MemoryStream stream, StreamManifest manifest)> GetTrackStream(string trackId, AudioQuality quality, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var trackStreamData = await GetTrackStreamData(trackId, quality, token);
        var streamManifest = new StreamManifest(trackStreamData);

        var urls = streamManifest.Urls;

        var outStream = new MemoryStream();

        for (int i = 0; i < urls.Length; i++)
        {
            var url = urls[i];

            var request = _client
                .BuildRequest(url);
            var response = await _client.ProcessRequestAsync(request);

            if (response.HasHttpError)
            {
                // signed stream urls expire; never let an error body (e.g. CloudFront AccessDenied XML)
                // masquerade as audio data. drop the cached manifest so a retry fetches a fresh one.
                lock (_cachedStreamData)
                    _cachedStreamData.Remove((trackId, quality));
                throw new UnavailableMediaException($"Segment {i + 1}/{urls.Length} of track {trackId} returned HTTP {(int)response.StatusCode}; stream urls likely expired.");
            }

            outStream.Write(response.ResponseData);
            onChunkDownloaded?.Invoke(i+1);
        }

        // TODO: test decryption, don't know of any tracks yet that need it

        if (!string.IsNullOrEmpty(streamManifest.EncryptionKey))
        {
            var (key, nonce) = Decryption.DecryptSecurityToken(streamManifest.EncryptionKey);
            var decryptedStream = new MemoryStream();
            Decryption.DecryptStream(outStream, decryptedStream, key, nonce);

            decryptedStream.Seek(0, SeekOrigin.Begin);
            await outStream.DisposeAsync();
            return (decryptedStream, streamManifest);
        }

        outStream.Seek(0, SeekOrigin.Begin);
        return (outStream, streamManifest);
    }

    private async Task<TrackStreamData> GetTrackStreamData(string trackId, AudioQuality quality, CancellationToken token = default)
    {
        lock (_cachedStreamData)
        {
            if (_cachedStreamData.TryGetValue((trackId, quality), out var cached) && DateTime.UtcNow < cached.expiresAt)
                return cached.data;
        }

        var result = await _api.Call(HttpMethod.Get, $"tracks/{trackId}/playbackinfopostpaywall",
            urlParameters: new()
            {
                { "playbackmode", "STREAM" },
                { "assetpresentation", "FULL" },
                { "audioquality", $"{quality}" }
            },
            token: token
        );
        var streamData = result.ToObject<TrackStreamData>()!;
        lock (_cachedStreamData)
            _cachedStreamData[(trackId, quality)] = (streamData, DateTime.UtcNow + _streamDataTtl);
        return streamData;
    }

    // manifests contain CloudFront-signed urls with a short (~1h) expiry; a stale entry poisons
    // every retry with guaranteed AccessDenied responses, so keep entries well below that.
    private static readonly TimeSpan _streamDataTtl = TimeSpan.FromMinutes(10);

    private Dictionary<(string trackId, AudioQuality quality), (TrackStreamData data, DateTime expiresAt)> _cachedStreamData = [];
}

public class DownloadData<T>(T data, string fileExtension) : IDisposable
{
    public T Data { get; set; } = data;
    public string FileExtension { get; set; } = fileExtension;

    public void Dispose()
    {
        if (Data is Stream stream)
            stream.Dispose();

        GC.SuppressFinalize(this);
    }
}
