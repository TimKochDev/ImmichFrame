using System.Net;
using System.Text.Json;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Logic;

[TestFixture]
public class PooledAlbumSelectionTests
{
    private sealed class AlbumHandler : HttpMessageHandler
    {
        public List<AssetResponseDto> Assets { get; } = Enumerable.Range(0, 53)
            .Select(_ => new AssetResponseDto
            {
                Id = Guid.NewGuid(),
                Type = AssetTypeEnum.IMAGE,
                Checksum = "test-checksum",
                OriginalPath = "/photos/test.jpg",
                OriginalFileName = "test.jpg",
                Visibility = AssetVisibility.Timeline
            }).ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(request.RequestUri!.AbsolutePath, Does.EndWith("/search/metadata"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new SearchResponseDto
                {
                    Assets = new SearchAssetResponseDto { Items = Assets, Total = Assets.Count }
                }))
            });
        }
    }

    [Test]
    public async Task GetNextAsset_NativeClientDoesNotRepeatWithinACycle()
    {
        using var handler = new AlbumHandler();
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var account = new Mock<IAccountSettings>();
        account.SetupGet(a => a.ImmichServerUrl).Returns("https://immich.example");
        account.SetupGet(a => a.ApiKey).Returns("test-key");
        account.SetupGet(a => a.Albums).Returns(new List<Guid> { Guid.NewGuid() });
        account.SetupGet(a => a.ExcludedAlbums).Returns(new List<Guid>());
        var general = new Mock<IGeneralSettings>();
        general.SetupGet(g => g.RefreshAlbumPeopleInterval).Returns(0);
        using var logic = new PooledImmichFrameLogic(account.Object, general.Object, factory.Object);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            var ids = new List<Guid>();
            for (var index = 0; index < handler.Assets.Count; index++)
            {
                var asset = await logic.GetNextAsset();
                Assert.That(asset, Is.Not.Null);
                ids.Add(asset!.Id);
            }
            Assert.That(ids, Is.EquivalentTo(handler.Assets.Select(a => a.Id)));
        }
    }

    [Test]
    public async Task GetAssets_PreservesAlbumCycleBoundaries()
    {
        using var handler = new AlbumHandler();
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var account = new Mock<IAccountSettings>();
        account.SetupGet(a => a.ImmichServerUrl).Returns("https://immich.example");
        account.SetupGet(a => a.ApiKey).Returns("test-key");
        account.SetupGet(a => a.Albums).Returns(new List<Guid> { Guid.NewGuid() });
        account.SetupGet(a => a.ExcludedAlbums).Returns(new List<Guid>());
        var general = new Mock<IGeneralSettings>();
        general.SetupGet(g => g.RefreshAlbumPeopleInterval).Returns(0);
        using var logic = new PooledImmichFrameLogic(account.Object, general.Object, factory.Object);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            var ids = new List<Guid>();
            foreach (var expectedCount in new[] { 25, 25, 3 })
            {
                var batch = (await logic.GetAssets()).ToList();
                Assert.That(batch.Count, Is.EqualTo(expectedCount));
                ids.AddRange(batch.Select(a => a.Id));
            }
            Assert.That(ids, Is.EquivalentTo(handler.Assets.Select(a => a.Id)));
        }
    }
}
