using NUnit.Framework;
using Moq;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;

namespace ImmichFrame.Core.Tests.Logic.Pool;

[TestFixture]
public class AlbumAssetsPoolTests
{
    private Mock<IApiCache> _mockApiCache;
    private Mock<ImmichApi> _mockImmichApi;
    private Mock<IAccountSettings> _mockAccountSettings;
    private AlbumAssetsPool _albumAssetsPool;

    [SetUp]
    public void Setup()
    {
        _mockApiCache = new Mock<IApiCache>();

        _mockApiCache
            .Setup(m => m.GetOrAddAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>((_, factory) => factory());

        _mockImmichApi = new Mock<ImmichApi>("", null);
        _mockAccountSettings = new Mock<IAccountSettings>();
        _albumAssetsPool = new AlbumAssetsPool(_mockApiCache.Object, _mockImmichApi.Object, _mockAccountSettings.Object);

        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>());
    }

    private AssetResponseDto CreateAsset(string id) => new AssetResponseDto { Id = FixtureHelpers.GuidFor(id), Type = AssetTypeEnum.IMAGE };

    private List<AssetResponseDto> SetAlbumAssets(int count)
    {
        var assets = Enumerable.Range(0, count)
            .Select(_ => new AssetResponseDto { Id = Guid.NewGuid(), Type = AssetTypeEnum.IMAGE }).ToList();
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { Guid.NewGuid() });
        foreach (var page in Enumerable.Range(1, Math.Max(1, (count + 999) / 1000)))
        {
            _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.Is<MetadataSearchDto>(query => query.Page == page), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new SearchResponseDto
                {
                    Assets = new SearchAssetResponseDto
                    {
                        Items = assets.Skip((page - 1) * 1000).Take(1000).ToList(),
                        Total = assets.Count
                    }
                });
        }
        return assets;
    }

    [TestCase(53)]
    [TestCase(832)]
    [TestCase(1205)]
    public async Task GetAssets_ShowsEveryPhotoOncePerCycle(int count)
    {
        var assets = SetAlbumAssets(count);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var actual = new List<Guid>();
            while (actual.Count < count)
            {
                var batch = (await _albumAssetsPool.GetAssets(25)).Select(a => a.Id).ToList();
                Assert.That(batch.Count, Is.EqualTo(Math.Min(25, count - actual.Count)));
                actual.AddRange(batch);
            }
            Assert.That(actual, Is.EquivalentTo(assets.Select(a => a.Id)));
        }
    }

    [Test]
    public async Task GetAssets_RemovesDuplicateIds()
    {
        var assets = SetAlbumAssets(3);
        assets.Add(assets[0]);
        var batch = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(batch.Count, Is.EqualTo(3));
        Assert.That(batch.Select(a => a.Id), Is.Unique);
    }

    [Test]
    public async Task GetAssets_SkipsDeletedPhotosAndAddsNewPhotosInNextCycle()
    {
        var assets = SetAlbumAssets(6);
        var first = (await _albumAssetsPool.GetAssets(2)).Select(a => a.Id).ToHashSet();
        var removed = assets.First(a => !first.Contains(a.Id));
        assets.Remove(removed);
        var added = new AssetResponseDto { Id = Guid.NewGuid(), Type = AssetTypeEnum.IMAGE };
        assets.Add(added);

        var rest = (await _albumAssetsPool.GetAssets(25)).Select(a => a.Id).ToList();
        Assert.That(rest, Is.EquivalentTo(assets.Where(a => !first.Contains(a.Id) && a.Id != added.Id).Select(a => a.Id)));
        var next = (await _albumAssetsPool.GetAssets(25)).Select(a => a.Id).ToList();
        Assert.That(next, Is.EquivalentTo(assets.Select(a => a.Id)));
    }

    [Test]
    public async Task GetAssets_HandlesAnEmptyAlbumAndLaterPhotos()
    {
        var assets = SetAlbumAssets(3);
        await _albumAssetsPool.GetAssets(1);
        assets.Clear();
        Assert.That(await _albumAssetsPool.GetAssets(25), Is.Empty);
        assets.Add(new AssetResponseDto { Id = Guid.NewGuid(), Type = AssetTypeEnum.IMAGE });
        Assert.That((await _albumAssetsPool.GetAssets(25)).Select(a => a.Id), Is.EqualTo(assets.Select(a => a.Id)));
    }

    [Test]
    public async Task GetAssets_ConcurrentBatchesDoNotRepeatPhotos()
    {
        var assets = SetAlbumAssets(100);
        var batches = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => _albumAssetsPool.GetAssets(25))));
        Assert.That(batches.SelectMany(batch => batch).Select(a => a.Id),
            Is.EquivalentTo(assets.Select(a => a.Id)));
    }

    [Test]
    public async Task LoadAssets_ReturnsAssetsPresentIIncludedNotExcludedAlbums()
    {
        // Arrange
        var album1Id = Guid.NewGuid();
        var excludedAlbumId = Guid.NewGuid();

        var assetA = CreateAsset("A"); // In album1
        var assetB = CreateAsset("B"); // In album1 and excludedAlbum
        var assetC = CreateAsset("C"); // In excludedAlbum only
        var assetD = CreateAsset("D"); // In album1 only

        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { album1Id });
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });

        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album1Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { assetA, assetB, assetD }, Total = 3 } });
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(excludedAlbumId)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { assetB, assetC }, Total = 2 } });

        // Act
        var result = (await _albumAssetsPool.GetAssets(25)).ToList();

        // Assert
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("A")));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("D")));
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album1Id)), It.IsAny<CancellationToken>()), Times.Once);
        _mockImmichApi.Verify(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(excludedAlbumId)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_NoIncludedAlbums_ReturnsEmpty()
    {
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { Guid.NewGuid() });
        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MetadataSearchDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { CreateAsset("excluded_only") }, Total = 1 } });


        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task LoadAssets_NoExcludedAlbums_ReturnsAlbums()
    {
        var album1Id = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { album1Id });
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>()); // Empty excluded

        _mockImmichApi.Setup(api => api.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<string>(), It.Is<MetadataSearchDto>(d => d.AlbumIds.Contains(album1Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResponseDto { Assets = new SearchAssetResponseDto { Items = new List<AssetResponseDto> { CreateAsset("A") }, Total = 1 } });

        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result.Any(a => a.Id == FixtureHelpers.GuidFor("A")));
    }

    [Test]
    public async Task LoadAssets_NullAlbums_ReturnsEmpty()
    {
        _mockAccountSettings.SetupGet(s => s.Albums).Returns((List<Guid>)null);

        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result, Is.Empty);

        // the absence of an error, whereas before a null pointer exception would be thrown, indicates success.
    }

    [Test]
    public async Task LoadAssets_NullExcludedAlbums_Succeeds()
    {
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns((List<Guid>)null);

        var result = (await _albumAssetsPool.GetAssets(25)).ToList();
        Assert.That(result, Is.Empty);

        // the absence of an error, whereas before a null pointer exception would be thrown, indicates success.
    }
}
