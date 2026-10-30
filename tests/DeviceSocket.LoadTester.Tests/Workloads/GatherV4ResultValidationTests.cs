using System.Net;
using System.Text;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Workloads.Gather;

namespace DeviceSocket.LoadTester.Tests.Workloads;

/// <summary>验证 GatherV4 的 HTTP 成功不能替代本表有效结果与完整采集项覆盖。</summary>
public sealed class GatherV4ResultValidationTests
{
    /// <summary>核对空结果、部分结果、错误电表、F2 无效结果与完整结果，确保失败原因可区分。</summary>
    /// <param name="json">服务端可能返回的 JSON。</param>
    /// <param name="expectedError">期望稳定错误码，空表示成功。</param>
    [Theory]
    [InlineData("{\"Status\":true}", "API_GATHERV4_EMPTY_RESULT")]
    [InlineData("{\"Status\":true,\"Data\":[]}", "API_GATHERV4_EMPTY_RESULT")]
    [InlineData("{\"Status\":true,\"Data\":[{\"MeterId\":7002,\"ValidData\":true,\"DataType\":\"0C_129\"}]}", "API_GATHERV4_RESULT_DEVICE_MISMATCH")]
    [InlineData("{\"Status\":true,\"Data\":[{\"MeterId\":7001,\"ValidData\":false,\"DataType\":\"0C_129\"}]}", "API_GATHERV4_INVALID_RESULT")]
    [InlineData("{\"Status\":true,\"Data\":[{\"MeterId\":7001,\"ValidData\":true,\"DataType\":\"0C_129\"}]}", "API_GATHERV4_PARTIAL_RESULT")]
    [InlineData("{\"status\":true,\"data\":[{\"meterId\":7001,\"validData\":true,\"dataType\":\"0C_129\"},{\"meterId\":7001,\"validData\":true,\"dataType\":\"0C_130\"}]}", null)]
    [InlineData("[]", "API_INVALID_RESPONSE")]
    public void CompleteResult_IsRequired(string json, string? expectedError)
    {
        var device = DeviceAddressAllocator.Allocate(TestOptionsFactory.Create())[0];
        var adapter = new GatherV4CommandWorkloadAdapter(new GatherV4CommandOptions { ItemCodes = ["0C_129", "0C_130"] });
        var result = adapter.EvaluateResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(json), new GatherDeviceMeterMapping(device, 7001));
        Assert.Equal(expectedError is null, result.Succeeded);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.False(result.PrevalidationRejected);
    }

    /// <summary>确认透明转发可显式配置返回 DataType，不把 00_1 与请求业务项隐式等同。</summary>
    [Fact]
    public void ExplicitReturnedItemCode_IsUsed()
    {
        var device = DeviceAddressAllocator.Allocate(TestOptionsFactory.Create())[0];
        var adapter = new GatherV4CommandWorkloadAdapter(new GatherV4CommandOptions
        { ItemCodes = ["16_1"], ExpectedResultItemCodes = ["00_1"] });
        var json = "{\"Status\":true,\"Data\":[{\"MeterId\":7001,\"ValidData\":true,\"DataType\":\"00_1\"}]}";
        Assert.True(adapter.EvaluateResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(json), new GatherDeviceMeterMapping(device, 7001)).Succeeded);
    }
}
