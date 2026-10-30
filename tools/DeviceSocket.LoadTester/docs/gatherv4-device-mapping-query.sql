/*
用途：导出 DeviceSocket.LoadTester 使用的 GatherV4 真实设备映射。

输出列：
  meterId,deviceId

使用前必须修改：
  1. @GatherCode：与 GatherV4 Communication、IssueWebApi 及压测场景完全一致；
  2. @RequiredCount：一万台填 10000，五万台填 50000。

兼容性：
  仅使用 SQL Server 2005 及以上版本已有的 ROW_NUMBER、CONVERT、LIKE、RAISERROR，
  不使用 TRY_CONVERT、DROP TABLE IF EXISTS、STRING_AGG 等新版本语法。

筛选依据：
  与 GatherV4 GatherService.DBService.AmmeterDal.GetAmmeter 的有效关联保持一致：
  - 集中器 State >= 0 且 RemoveState >= 0；
  - 电表 State >= 0 且 State < 100；
  - 电表至少存在一条 State >= 0 的 TB_AmmeterGatherItem；
  - 排除 Special = 1 的特殊表，并要求测量点 MeteringCode 在 1 至 2040；
  - 只导出指定 GatherCode 下的设备；
  - 行政区域码必须是四位数字且不能以 0 开头；
  - 终端地址必须为一至五位数字，导出时左侧补零到五位；00000 作为真实特殊地址保留；
  - 每个集中器只取 ID 最小的有效电表；最终 deviceId 重复时只保留一条。
*/

SET NOCOUNT ON;

DECLARE @GatherCode varchar(100);
DECLARE @RequiredCount int;

SET @GatherCode = 'V4-Gather-8890';
SET @RequiredCount = 50000;

IF @RequiredCount <= 0
BEGIN
    RAISERROR(N'@RequiredCount 必须大于 0。', 16, 1);
    RETURN;
END;

IF LTRIM(RTRIM(@GatherCode)) = ''
BEGIN
    RAISERROR(N'@GatherCode 不能为空。', 16, 1);
    RETURN;
END;

IF OBJECT_ID('tempdb..#GatherV4LoadTestDevices') IS NOT NULL
    DROP TABLE #GatherV4LoadTestDevices;

;WITH NormalizedFocus AS
(
    SELECT
        F.ID AS FocusID,
        LTRIM(RTRIM(CONVERT(varchar(20), F.AreaCode))) AS AreaCodeText,
        LTRIM(RTRIM(CONVERT(varchar(20), F.[Address]))) AS AddressText,
        LTRIM(RTRIM(CONVERT(varchar(100), G.GatherCode))) AS GatherCode
    FROM dbo.TB_FocusInfo AS F
    INNER JOIN dbo.TB_GatherInfo AS G
        ON G.ID = F.GatherInfoID
    WHERE F.State >= 0
      AND F.RemoveState >= 0
),
ValidFocus AS
(
    SELECT
        FocusID,
        AreaCodeText,
        RIGHT('00000' + AddressText, 5) AS AddressText,
        GatherCode
    FROM NormalizedFocus
    WHERE GatherCode = @GatherCode
      AND LEN(AreaCodeText) = 4
      AND AreaCodeText NOT LIKE '%[^0-9]%'
      AND LEFT(AreaCodeText, 1) LIKE '[1-9]'
      AND LEN(AddressText) BETWEEN 1 AND 5
      AND AddressText NOT LIKE '%[^0-9]%'
),
EligibleMeter AS
(
    SELECT
        A.ID AS meterId,
        F.FocusID,
        F.AreaCodeText + F.AddressText AS deviceId,
        F.GatherCode,
        ROW_NUMBER() OVER
        (
            PARTITION BY F.FocusID
            ORDER BY A.ID ASC
        ) AS MeterRow
    FROM ValidFocus AS F
    INNER JOIN dbo.TB_AmmeterInfo AS A
        ON A.FocusID = F.FocusID
       AND A.ID > 0
       AND A.State >= 0
       AND A.State < 100
       AND ISNULL(A.Special, 0) <> 1
       AND A.MeteringCode BETWEEN 1 AND 2040
    WHERE EXISTS
    (
        SELECT 1
        FROM dbo.TB_AmmeterGatherItem AS D
        WHERE D.AmmeterID = A.ID
          AND D.State >= 0
    )
),
FirstMeterPerFocus AS
(
    SELECT
        meterId,
        FocusID,
        deviceId,
        GatherCode
    FROM EligibleMeter
    WHERE MeterRow = 1
),
UniqueDevice AS
(
    SELECT
        meterId,
        FocusID,
        deviceId,
        GatherCode,
        ROW_NUMBER() OVER
        (
            PARTITION BY deviceId
            ORDER BY FocusID ASC, meterId ASC
        ) AS DeviceRow
    FROM FirstMeterPerFocus
)
SELECT
    meterId,
    deviceId,
    FocusID,
    GatherCode
INTO #GatherV4LoadTestDevices
FROM UniqueDevice
WHERE DeviceRow = 1;

DECLARE @EligibleCount int;
SELECT @EligibleCount = COUNT(*)
FROM #GatherV4LoadTestDevices;

/* 先返回数量检查；eligibleCount 小于 requestedCount 时禁止继续导出。 */
SELECT
    @GatherCode AS gatherCode,
    @RequiredCount AS requestedCount,
    @EligibleCount AS eligibleCount,
    @EligibleCount - @RequiredCount AS remainingCount;

IF @EligibleCount < @RequiredCount
BEGIN
    RAISERROR(N'符合 GatherV4 人工下发条件的唯一设备数量不足，请检查 GatherCode、档案状态和采集项配置。', 16, 1);
    RETURN;
END;

/*
最终 CSV 只保留下面两个字段，列名必须保持 meterId、deviceId。
排序固定，保证所有负载机使用同一文件时 shardIndex 分配稳定。
*/
SELECT TOP (@RequiredCount)
    meterId,
    deviceId
FROM #GatherV4LoadTestDevices
ORDER BY deviceId ASC, meterId ASC;

/*
可选：排查 API 返回“表信息不存在于缓存中”的 meterId。
把 ID 替换为 http-errors.csv 中的失败值。无结果表示电表主键不存在；有结果时继续核对
电表/集中器状态、GatherCode、MeteringCode、Special 和 hasEnabledGatherItem。

SELECT
    C.ID AS meterId,
    C.State AS ammeterState,
    C.Special,
    C.MeteringCode,
    B.ID AS focusId,
    B.State AS focusState,
    B.RemoveState,
    G.GatherCode,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM dbo.TB_AmmeterGatherItem AS D
        WHERE D.AmmeterID = C.ID
          AND D.State >= 0
    ) THEN 1 ELSE 0 END AS hasEnabledGatherItem
FROM dbo.TB_AmmeterInfo AS C
LEFT JOIN dbo.TB_FocusInfo AS B
    ON B.ID = C.FocusID
LEFT JOIN dbo.TB_GatherInfo AS G
    ON G.ID = B.GatherInfoID
WHERE C.ID IN (28082, 44220, 75251, 1731)
ORDER BY C.ID;
*/

