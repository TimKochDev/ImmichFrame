using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public class AlbumAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : CachingApiAssetsPool(apiCache, immichApi, accountSettings)
{
    private readonly SemaphoreSlim _selectionLock = new(1, 1);
    private Queue<Guid> _remaining = new();

    public override async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        if (requested <= 0)
            return Array.Empty<AssetResponseDto>();

        await _selectionLock.WaitAsync(ct);
        try
        {
            var current = (await AllAssets(ct)).DistinctBy(asset => asset.Id)
                .ToDictionary(asset => asset.Id);

            // Remove deleted or excluded photos before the next batch.
            _remaining = new Queue<Guid>(_remaining.Where(current.ContainsKey));
            if (_remaining.Count == 0)
            {
                var ids = current.Keys.ToArray();
                Random.Shared.Shuffle(ids);
                _remaining = new Queue<Guid>(ids);
            }

            // End the batch at the cycle boundary. The client can reorder a batch.
            var result = new List<AssetResponseDto>(Math.Min(requested, _remaining.Count));
            while (result.Count < requested && _remaining.TryDequeue(out var id))
                result.Add(current[id]);

            return result;
        }
        finally
        {
            _selectionLock.Release();
        }
    }

    protected override async Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default)
    {
        var albumAssets = new List<AssetResponseDto>();

        var albums = accountSettings.Albums;
        if (albums != null)
        {
            foreach (var albumId in albums)
            {
                int page = 1;
                int batchSize = 1000;
                int itemsInPage;
                do
                {
                    var metadataBody = new MetadataSearchDto
                    {
                        Page = page,
                        Size = batchSize,
                        AlbumIds = [albumId],
                        WithExif = true,
                        WithPeople = true,
                    };
                    var searchResponse = await immichApi.SearchAssetsAsync(null, null, metadataBody, ct);

                    itemsInPage = searchResponse.Assets.Items.Count;

                    albumAssets.AddRange(searchResponse.Assets.Items);
                    page++;
                } while (itemsInPage == batchSize);
            }
        }

        return albumAssets;
    }
}
