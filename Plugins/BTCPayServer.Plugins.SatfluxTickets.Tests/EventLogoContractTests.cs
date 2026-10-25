using System.Reflection;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.SatfluxTickets.Controllers;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class EventLogoContractTests
{
    [Fact]
    public async Task Event_response_resolves_storage_url_instead_of_guessing_from_file_id()
    {
        var files = new RecordingFiles { ResolvedUrl = "https://cdn.example.test/media/storage-name.png" };
        var controller = CreateController(files);
        var dto = await Map(controller, new Event
        {
            Id = "event-one", StoreId = "store-one", EventLogo = "opaque-file-id", EventState = EntityState.Disabled
        });

        Assert.Equal("opaque-file-id", dto.EventLogoFileId);
        Assert.Equal(files.ResolvedUrl, dto.EventLogoUrl);
        Assert.Equal("opaque-file-id", Assert.Single(files.Resolutions).FileId);
        Assert.Equal("https://btcpay.example.test/", files.Resolutions[0].Root.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Removed_logo_returns_empty_id_without_resolving_a_file(string? fileId)
    {
        var files = new RecordingFiles();
        var dto = await Map(CreateController(files), new Event { EventLogo = fileId!, EventState = EntityState.Disabled });
        Assert.Equal(string.Empty, dto.EventLogoFileId);
        Assert.Null(dto.EventLogoUrl);
        Assert.Empty(files.Resolutions);
    }

    [Fact]
    public async Task Missing_storage_url_is_not_fabricated_from_the_opaque_file_id()
    {
        var files = new RecordingFiles { ResolvedUrl = null };
        var dto = await Map(CreateController(files), new Event { EventLogo = "missing-file", EventState = EntityState.Disabled });
        Assert.Equal("missing-file", dto.EventLogoFileId);
        Assert.Null(dto.EventLogoUrl);
        Assert.Single(files.Resolutions);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("delete")]
    public async Task Logo_operations_reject_a_store_different_from_authorized_context_before_storage(string operation)
    {
        var files = new RecordingFiles();
        var controller = CreateController(files);
        controller.HttpContext.SetStoreData(new StoreData { Id = "owned-store" });

        var result = operation == "upload"
            ? await controller.UploadEventLogo("foreign-store", "foreign-event", File("image/png", Png()))
            : await controller.DeleteEventLogo("foreign-store", "foreign-event");

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(files.Resolutions);
        Assert.Equal(0, files.Writes);
    }

    [Theory]
    [InlineData("image/svg+xml", "<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("image/png", "<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("text/plain", "not an image")]
    public async Task Unsupported_or_spoofed_images_are_rejected_before_database_or_storage(string mime, string content)
    {
        var files = new RecordingFiles();
        var controller = CreateController(files);
        controller.HttpContext.SetStoreData(new StoreData { Id = "owned-store" });
        var result = await controller.UploadEventLogo("owned-store", "event-one", File(mime, System.Text.Encoding.UTF8.GetBytes(content)));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, files.Writes);
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpeg")]
    [InlineData("image/gif", "gif")]
    [InlineData("image/webp", "webp")]
    public async Task Raster_validation_requires_the_mime_to_match_supported_magic_bytes(string mime, string format)
    {
        var bytes = format switch
        {
            "png" => Png(),
            "jpeg" => new byte[] { 255, 216, 255, 224 },
            "gif" => "GIF89a"u8.ToArray(),
            _ => "RIFFxxxxWEBP"u8.ToArray()
        };
        Assert.True(await Validate(File(mime, bytes)));
        Assert.False(await Validate(File("image/svg+xml", bytes)));
    }

    [Theory]
    [InlineData(2 * 1024 * 1024, true)]
    [InlineData(2 * 1024 * 1024 + 1, false)]
    [InlineData(0, false)]
    public async Task Raster_validation_enforces_the_same_two_mebibyte_boundary_as_satflux(int size, bool expected)
    {
        var bytes = new byte[size];
        if (size >= 8)
            Png().CopyTo(bytes, 0);
        Assert.Equal(expected, await Validate(File("image/png", bytes)));
    }

    private static GreenfieldSatfluxTicketsEventsController CreateController(IFileService files)
    {
        var controller = new GreenfieldSatfluxTicketsEventsController(null!, files, null!, null!, null!);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("btcpay.example.test");
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static Task<EventData> Map(GreenfieldSatfluxTicketsEventsController controller, Event entity)
        => (Task<EventData>)typeof(GreenfieldSatfluxTicketsEventsController)
            .GetMethod("ToEventData", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(controller, [entity, 0])!;

    private static Task<bool> Validate(IFormFile file)
        => (Task<bool>)typeof(GreenfieldSatfluxTicketsEventsController)
            .GetMethod("IsSupportedLogoImage", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [file])!;

    private static byte[] Png() => [137, 80, 78, 71, 13, 10, 26, 10];

    private static FormFile File(string mime, byte[] bytes)
        => new(new MemoryStream(bytes), 0, bytes.Length, "file", "event-image.png")
        {
            Headers = new HeaderDictionary(), ContentType = mime
        };

    private sealed class RecordingFiles : IFileService
    {
        public string? ResolvedUrl { get; init; }
        public List<(Uri Root, string FileId)> Resolutions { get; } = [];
        public int Writes { get; private set; }
        public Task<string?> GetFileUrl(Uri baseUri, string fileId)
        {
            Resolutions.Add((baseUri, fileId));
            return Task.FromResult(ResolvedUrl);
        }
        public Task<bool> IsAvailable() => Task.FromResult(true);
        public Task<IStoredFile> AddFile(IFormFile file, string userId) => throw new InvalidOperationException("Unexpected storage write");
        public Task RemoveFile(string fileId, string userId)
        {
            Writes++;
            throw new InvalidOperationException("Unexpected storage write");
        }
        public Task<UploadImageResultModel> UploadImage(IFormFile file, string userId, long maxFileSizeInBytes = 1_000_000)
        {
            Writes++;
            throw new InvalidOperationException("Unexpected storage write");
        }
    }
}
