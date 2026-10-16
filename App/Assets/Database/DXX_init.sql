-- ============================================================
-- DXX 数据库初始化脚本 (PostgreSQL)
-- 说明：本脚本用于手动建库建表，配合 App 中“自动建表已注释”使用。
-- 执行顺序：先建库（在 postgres 库下执行 DXX_create_database.sql），再建表（切到 DXX 库执行本文件）。
-- 重要：所有表名、列名、索引名一律【小写】。
--       SqlSugar 在 PostgreSQL 模式下会将标识符转小写并加双引号（如 users/id/username），
--       若按大写建表，程序查询会报 “关系 xx 不存在” (42P01)。切勿改回大写。
-- ============================================================

-- ============================================================
-- 0. 清理可能存在的旧版大写表（避免大小写混用；若为全新库可忽略）
--    注意：此段会删除已有业务数据，全新库跳过即可。
-- ============================================================
-- DROP TABLE IF EXISTS "Users" CASCADE;
-- DROP TABLE IF EXISTS "ProductTestRecords" CASCADE;
-- DROP TABLE IF EXISTS "PerformanceTestData" CASCADE;
-- DROP TABLE IF EXISTS "AlarmConfigs" CASCADE;
-- DROP TABLE IF EXISTS "AlarmRecords" CASCADE;
-- DROP TABLE IF EXISTS "CountRecords_20260701" CASCADE;
-- DROP TABLE IF EXISTS "DeviceStatusRecords_20260701" CASCADE;
-- DROP TABLE IF EXISTS "DisplacementPressureRecords_20260701" CASCADE;

-- ============================================================
-- 1. 用户表（对应 AppZC.Models.DB.User）
-- ============================================================
CREATE TABLE IF NOT EXISTS users (
    id              SERIAL       PRIMARY KEY,                                  -- 主键，自增
    username        VARCHAR(50)  NOT NULL UNIQUE,                              -- 用户名（内部标识；厂商登录固定用 vendor）
    realname        VARCHAR(50)  NOT NULL,                                     -- 真实姓名
    fingerprintid   VARCHAR(50)  NULL UNIQUE,                                  -- 指纹ID（唯一，可空）
    cardid          VARCHAR(50)  NULL,                                         -- 卡号（刷卡登录，可空）
    password        VARCHAR(100) NULL,                                         -- 密码（哈希存储，可空）
    role            INT           NOT NULL DEFAULT 1,                          -- 角色：1=操作员,2=技术员,3=PE,4=ME,5=管理员,6=厂商
    isenabled       BOOLEAN       NOT NULL DEFAULT TRUE,                       -- 是否启用
    createdat       TIMESTAMP     NOT NULL DEFAULT CURRENT_TIMESTAMP,          -- 创建时间
    lastloginat     TIMESTAMP     NULL                                         -- 最后登录时间
);

CREATE INDEX IF NOT EXISTS ix_users_fingerprintid ON users(fingerprintid);
CREATE INDEX IF NOT EXISTS ix_users_role          ON users(role);
CREATE INDEX IF NOT EXISTS ix_users_isenabled     ON users(isenabled);

-- ============================================================
-- 2. 性能测试数据表（对应 PerformanceTestData）
-- ============================================================
CREATE TABLE IF NOT EXISTS performancetestdata (
    id           SERIAL      PRIMARY KEY,
    createtime   TIMESTAMP   NOT NULL,                 -- 插入时由数据库时间填充
    testflag     SMALLINT    NOT NULL,                 -- 1=GRR,2=CORR,3=相机静态,4=轴重复性
    devicestatus SMALLINT   NULL,                      -- 设备运行状态
    sncode       VARCHAR(100) NULL,                    -- SN码
    testdatajson TEXT       NULL                       -- 性能测试数据(JSON)
);

-- ============================================================
-- 3. 报警配置表（对应 AlarmConfig -> 表名 AlarmConfigs）
-- ============================================================
CREATE TABLE IF NOT EXISTS alarmconfigs (
    id          SERIAL      PRIMARY KEY,
    address     VARCHAR(20) NOT NULL,                 -- PLC地址(D4231.0)
    alarmtype   VARCHAR(20) NOT NULL,                 -- Alarm/AlarmE/Warning
    description VARCHAR(200) NOT NULL,                -- 报警描述
    alarmcode   VARCHAR(50) NULL,                     -- 报警代码(MES上报)
    solution    VARCHAR(500) NULL,                    -- 解决方案
    isenabled   BOOLEAN     NOT NULL DEFAULT TRUE     -- 是否启用
);

-- ============================================================
-- 4. 报警记录表（对应 AlarmRecord -> 表名 AlarmRecords）
-- ============================================================
CREATE TABLE IF NOT EXISTS alarmrecords (
    id                SERIAL      PRIMARY KEY,
    address           VARCHAR(20) NOT NULL,
    alarmtype         VARCHAR(20) NOT NULL,
    description       VARCHAR(200) NOT NULL,
    alarmcode         VARCHAR(50) NULL,
    solution          VARCHAR(500) NULL,
    alarmtime         TIMESTAMP   NOT NULL,            -- 插入时由数据库时间填充
    isacknowledged    BOOLEAN     NOT NULL DEFAULT FALSE,-- 是否已确认
    acktime           TIMESTAMP   NULL                 -- 确认时间
);

-- ============================================================
-- 5. 分表（按月/日分表，SqlSugar 在首次写入时按 {year}{month}{day}
--    自动建物理子表，以下仅为示例模板；通常无需手工预建）
--    主键为雪花 long，禁用数据库自增。
-- ============================================================

-- 5.1 计数记录（CountRecord -> CountRecords_yyyymmdd）
CREATE TABLE IF NOT EXISTS countrecords_20260701 (
    id           BIGINT      PRIMARY KEY,              -- 雪花主键，非自增
    counttype    VARCHAR(20) NOT NULL,                 -- 产品出料_OK / 产品出料_NG
    triggertime  TIMESTAMP   NOT NULL,                 -- 触发时间(分表依据)
    sn           VARCHAR(200) NULL,
    barcode      VARCHAR(500) NULL,
    devicestatus SMALLINT    NULL,
    productsize  REAL        NULL,
    binnumber    VARCHAR(10)  NULL
);

-- 5.2 设备状态记录（DeviceStatusRecord -> DeviceStatusRecords_yyyymmdd）
CREATE TABLE IF NOT EXISTS devicestatusrecords_20260701 (
    id               BIGINT      PRIMARY KEY,
    devicestatus     SMALLINT    NOT NULL,             -- 2=暂停,3=故障,4=PM
    statusname       VARCHAR(20) NOT NULL,
    starttime        TIMESTAMP   NOT NULL,             -- 分表依据
    endtime          TIMESTAMP   NOT NULL,
    durationseconds  INT         NOT NULL,
    createtime       TIMESTAMP   NOT NULL
);

-- 5.4 位移压力记录（DisplacementPressureRecord -> DisplacementPressureRecords_yyyymmdd）
CREATE TABLE IF NOT EXISTS displacementpressurerecords_20260701 (
    id                 BIGINT      PRIMARY KEY,
    createtime         TIMESTAMP   NOT NULL,           -- 分表依据
    displacementvalue  DOUBLE PRECISION NOT NULL,
    pressurevalue      DOUBLE PRECISION NOT NULL,
    devicestatus       SMALLINT    NULL
);

-- ============================================================
-- 6. 单个产品检测结果主表（对应 AppZC.Models.DB.ProductTestRecord）
--    每工位一行，按 (productsn, station) 唯一；一个产品只做一次耐压 → 仅 withstand1*。
--    字段与 MES jcdata 协议 10 项一一对应，便于按产品编号合并与上报。
-- ============================================================
CREATE TABLE IF NOT EXISTS producttestrecords (
    id                     BIGINT       PRIMARY KEY,               -- 雪花主键，非自增
    productsn              VARCHAR(200) NOT NULL,                  -- 产品编号(SN)
    station                SMALLINT     NOT NULL,                  -- 1=耐压工位 2=后段(尺寸/喷码)
    withstand1result       VARCHAR(10)  NULL,                      -- 耐压结论 OK/NG
    withstand1current      DOUBLE PRECISION NULL,                  -- 耐压电流(mA)
    withstand1voltage      DOUBLE PRECISION NULL,                  -- 耐压电压(V)
    withstand1ramptime     DOUBLE PRECISION NULL,                  -- 耐压缓升时间(s)
    withstand1holdtime     DOUBLE PRECISION NULL,                  -- 耐压保压时间(s)
    withstandcompletedtime TIMESTAMP    NULL,                      -- 耐压检测完成时间
    slotindex               SMALLINT     NULL,                      -- 站内机位：耐压1-8，尺寸1-4
    dimlength              DOUBLE PRECISION NULL,                  -- 厚度(mm)
    dimwidth               DOUBLE PRECISION NULL,                  -- 宽度(mm)
    dimshoulderheight      DOUBLE PRECISION NULL,                  -- 高度不含极柱(mm)
    dimtotalheight         DOUBLE PRECISION NULL,                  -- 高度含极柱(mm)
    recipeid               INT          NULL,                      -- 判定所用配方ID
    dimlengthdelta         DOUBLE PRECISION NULL,                   -- 厚度变化量(mm)
    dimwidthdelta          DOUBLE PRECISION NULL,                   -- 宽度变化量(mm)
    dimshoulderheightdelta DOUBLE PRECISION NULL,                   -- 高度不含极柱变化量(mm)
    dimtotalheightdelta    DOUBLE PRECISION NULL,                   -- 高度含极柱变化量(mm)
    dimensioncompletedtime TIMESTAMP    NULL,                       -- 尺寸检测完成时间
    overallresult          VARCHAR(10)  NULL,                      -- 综合结论 OK/NG
    markslotindex          SMALLINT     NULL,                       -- 喷码机位1-2
    markinfocode           INT          NULL,                       -- PLC NG原因/信息编码（D5007/D5008）
    markresult             VARCHAR(10)  NULL,                       -- 喷码结果 OK/NG
    markcompletedtime      TIMESTAMP    NULL,                       -- 喷码完成时间
    employeeno             VARCHAR(50)  NULL,                      -- 员工编号/卡号
    mesuploaded            BOOLEAN      NULL,                      -- 是否已上传 MES
    mesuploadtime          TIMESTAMP    NULL,                      -- MES 上传时间
    createtime             TIMESTAMP    NOT NULL,                  -- 创建时间
    updatetime             TIMESTAMP    NULL                       -- 更新时间
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_producttestrecords_snstation
    ON producttestrecords(productsn, station);
CREATE INDEX IF NOT EXISTS ix_producttestrecords_productsn
    ON producttestrecords(productsn);
CREATE INDEX IF NOT EXISTS ix_producttestrecords_createtime
    ON producttestrecords(createtime);

-- ============================================================
-- 7. 配方表（对应 AppZC.Models.DB.Recipe）
--    配方为电芯尺寸数据单元：每配方一条，四列尺寸 mm
-- ============================================================
CREATE TABLE IF NOT EXISTS recipes (
    id                  INT          PRIMARY KEY,                          -- 配方ID（业务主键，界面手动设置，非自增）
    name                VARCHAR(100) NOT NULL UNIQUE,                      -- 配方名称（唯一）
    widthmm             DOUBLE PRECISION NOT NULL DEFAULT 0,               -- 宽度(mm)
    heightwithpolemm    DOUBLE PRECISION NOT NULL DEFAULT 0,               -- 高度含极柱(mm)
    heightwithoutpolemm DOUBLE PRECISION NOT NULL DEFAULT 0,               -- 高度不含极柱(mm)
    thicknessmm         DOUBLE PRECISION NOT NULL DEFAULT 0,               -- 厚度(mm)
    widthproducttolerancemm             DOUBLE PRECISION NOT NULL DEFAULT 0, -- 宽度产品公差(mm)，暂不参与判定
    heightwithpoleproducttolerancemm    DOUBLE PRECISION NOT NULL DEFAULT 0, -- 高度含极柱产品公差(mm)，暂不参与判定
    heightwithoutpoleproducttolerancemm DOUBLE PRECISION NOT NULL DEFAULT 0, -- 高度不含极柱产品公差(mm)，暂不参与判定
    thicknessproducttolerancemm         DOUBLE PRECISION NOT NULL DEFAULT 0, -- 厚度产品公差(mm)，暂不参与判定
    widthdeltamm             DOUBLE PRECISION NOT NULL DEFAULT 0,          -- 宽度变化量(mm)，手动标定写入，可正可负
    heightwithpoledeltamm    DOUBLE PRECISION NOT NULL DEFAULT 0,          -- 高度含极柱变化量(mm)，手动标定写入，可正可负
    heightwithoutpoledeltamm DOUBLE PRECISION NOT NULL DEFAULT 0,          -- 高度不含极柱变化量(mm)，手动标定写入，可正可负
    thicknessdeltamm         DOUBLE PRECISION NOT NULL DEFAULT 0,          -- 厚度变化量(mm)，手动标定写入，可正可负
    widthtolerancemm             DOUBLE PRECISION NOT NULL DEFAULT 0,      -- 宽度变化量判定范围（±mm），人工输入，≥0
    heightwithpoletolerancemm    DOUBLE PRECISION NOT NULL DEFAULT 0,      -- 高度含极柱变化量判定范围（±mm），人工输入，≥0
    heightwithoutpoletolerancemm DOUBLE PRECISION NOT NULL DEFAULT 0,      -- 高度不含极柱变化量判定范围（±mm），人工输入，≥0
    thicknesstolerancemm         DOUBLE PRECISION NOT NULL DEFAULT 0,      -- 厚度变化量判定范围（±mm），人工输入，≥0
    createtime          TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,   -- 创建时间
    updatetime          TIMESTAMP    NULL                                  -- 更新时间
);

-- ============================================================
-- 8. 传感器配置表（对应 AppZC.Models.DB.Sensor）
--    全局固定清单 50 条，按六分组；仅维护名称/地址
--    编号规划：1公共区=1-13、1A=14-19、1B=20-25、2公共区=26-38、2A=39-44、2B=45-50
-- ============================================================
CREATE TABLE IF NOT EXISTS sensors (
    id          SERIAL       PRIMARY KEY,                          -- 主键，自增
    sensorno    INT          NOT NULL UNIQUE,                       -- 传感器编号（业务唯一）
    name        VARCHAR(100) NOT NULL,                              -- 传感器名称
    address     VARCHAR(100) NULL,                                  -- 传感器地址
    groupcode   VARCHAR(20)  NOT NULL,                              -- 分组：1A/1B/1公共区/2A/2B/2公共区
    createtime  TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,    -- 创建时间
    updatetime  TIMESTAMP    NULL                                   -- 更新时间
);

CREATE INDEX IF NOT EXISTS ix_sensors_groupcode ON sensors(groupcode);

-- 50 条传感器种子数据（名称默认 传感器1..传感器50，地址默认空；重复执行幂等）
INSERT INTO sensors (sensorno, name, address, groupcode, createtime)
SELECT gs,
       '传感器' || gs::text,
       '',
       CASE
           WHEN gs BETWEEN 1 AND 13 THEN '1公共区'
           WHEN gs BETWEEN 14 AND 19 THEN '1A'
           WHEN gs BETWEEN 20 AND 25 THEN '1B'
           WHEN gs BETWEEN 26 AND 38 THEN '2公共区'
           WHEN gs BETWEEN 39 AND 44 THEN '2A'
           ELSE '2B'
       END,
       CURRENT_TIMESTAMP
FROM generate_series(1, 50) AS gs
ON CONFLICT (sensorno) DO NOTHING;

-- ============================================================
-- 9. 初始用户（厂商默认密码 1，权限最大；其余身份由 MES 刷卡下发权限，无需密码）
--    如不需要默认账户，可删除下面这段 INSERT。
-- ============================================================
INSERT INTO users (username, realname, cardid, password, role, isenabled, createdat)
SELECT 'admin',    '管理员', 'ADMIN001', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 5, TRUE, CURRENT_TIMESTAMP
WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = 'admin');

INSERT INTO users (username, realname, cardid, password, role, isenabled, createdat)
SELECT 'engineer','技术员',   'ENG001',   'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 2, TRUE, CURRENT_TIMESTAMP
WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = 'engineer');

INSERT INTO users (username, realname, cardid, password, role, isenabled, createdat)
SELECT 'operator', '操作员',   'OPR001',   'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 1, TRUE, CURRENT_TIMESTAMP
WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = 'operator');

INSERT INTO users (username, realname, cardid, password, role, isenabled, createdat)
SELECT 'vendor',   '厂商',     'VDR001',   'a4ayc/80/OGda4BO/1o/V0etpOqiLx1JwB5S3beHW0s=', 6, TRUE, CURRENT_TIMESTAMP
WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = 'vendor');

-- ============================================================
-- 说明
-- 1) 框架 KV 存储表（DbKeyValueItem）仍由程序自动创建（ZC.KvStorage 内部表，
--    非业务表），无需在此手工建；如需完全接管，请提供 ZC.KvStorage 程序集以确认结构。
-- 2) 分表（CountRecords/DeviceStatusRecords/...）由 SqlSugar 在首次写入时按 {year}{month}{day}
--    自动建物理子表，这是 ORM 分表机制决定的，注释 InitializeDatabaseTables 不影响该功能。
-- 3) 所有标识符必须小写，与 SqlSugar 在 PostgreSQL 下的映射命名一致（勿改回大写）。
-- ============================================================
