-- ============================================
-- 用户权限管理数据库表设计 (SQL Server)
-- ============================================

-- 1. 用户表
CREATE TABLE Users (
    Id INT PRIMARY KEY IDENTITY(1,1),                    -- 用户ID（主键，自增）
    UserName NVARCHAR(50) NOT NULL UNIQUE,               -- 用户名（唯一）
    RealName NVARCHAR(50) NOT NULL,                      -- 真实姓名
    FingerprintId NVARCHAR(50) NULL UNIQUE,                 -- 指纹ID（唯一，可为空）
    Password NVARCHAR(100) NULL,                         -- 密码（加密存储，可为空）
    Role INT NOT NULL DEFAULT 0,                         -- 角色：0=操作员, 1=工程师, 2=管理员
    IsEnabled BIT NOT NULL DEFAULT 1,                    -- 是否启用：1=启用, 0=禁用
    CreatedAt DATETIME NOT NULL DEFAULT GETDATE(),       -- 创建时间
    UpdatedAt DATETIME NULL,                             -- 更新时间
    LastLoginAt DATETIME NULL,                           -- 最后登录时间
    Remark NVARCHAR(200) NULL                            -- 备注
);

-- 创建索引
CREATE INDEX IX_Users_FingerprintId ON Users(FingerprintId);
CREATE INDEX IX_Users_Role ON Users(Role);
CREATE INDEX IX_Users_IsEnabled ON Users(IsEnabled);

-- 2. 登录日志表（可选，用于审计）
CREATE TABLE LoginLogs (
    Id INT PRIMARY KEY IDENTITY(1,1),                    -- 日志ID（主键，自增）
    UserId INT NOT NULL,                                 -- 用户ID
    UserName NVARCHAR(50) NOT NULL,                      -- 用户名
    LoginType NVARCHAR(20) NOT NULL,                     -- 登录方式：Password=密码登录, Fingerprint=指纹登录
    LoginTime DATETIME NOT NULL DEFAULT GETDATE(),       -- 登录时间
    LogoutTime DATETIME NULL,                            -- 登出时间
    LogoutType NVARCHAR(20) NULL,                        -- 登出方式：Manual=手动, Auto=自动（3分钟无操作）
    IpAddress NVARCHAR(50) NULL,                         -- IP地址（可选）
    IsSuccess BIT NOT NULL DEFAULT 1,                    -- 是否成功：1=成功, 0=失败
    FailReason NVARCHAR(200) NULL,                       -- 失败原因
    CONSTRAINT FK_LoginLogs_Users FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
);

-- 创建索引
CREATE INDEX IX_LoginLogs_UserId ON LoginLogs(UserId);
CREATE INDEX IX_LoginLogs_LoginTime ON LoginLogs(LoginTime);

-- 3. 权限变更日志表（可选，用于审计）
CREATE TABLE PermissionChangeLogs (
    Id INT PRIMARY KEY IDENTITY(1,1),                    -- 日志ID（主键，自增）
    UserId INT NOT NULL,                                 -- 被修改权限的用户ID
    UserName NVARCHAR(50) NOT NULL,                      -- 被修改权限的用户名
    OldRole INT NOT NULL,                                -- 原角色
    NewRole INT NOT NULL,                                -- 新角色
    OperatorId INT NULL,                                 -- 操作人ID
    OperatorName NVARCHAR(50) NULL,                      -- 操作人姓名
    ChangeTime DATETIME NOT NULL DEFAULT GETDATE(),      -- 变更时间
    Remark NVARCHAR(200) NULL,                           -- 备注
    CONSTRAINT FK_PermissionChangeLogs_Users FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
);

-- 创建索引
CREATE INDEX IX_PermissionChangeLogs_UserId ON PermissionChangeLogs(UserId);
CREATE INDEX IX_PermissionChangeLogs_ChangeTime ON PermissionChangeLogs(ChangeTime);

-- ============================================
-- 插入默认数据
-- ============================================

-- 插入默认管理员账户
INSERT INTO Users (UserName, RealName, CardNumber, Password, Role, IsEnabled, Remark)
VALUES 
    ('admin', '系统管理员', 'ADMIN001', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 2, 1, '默认管理员账户，密码：admin123'),
    ('engineer', '工程师', 'ENG001', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 1, 1, '默认工程师账户，密码：admin123'),
    ('operator', '操作员', 'OPR001', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 0, 1, '默认操作员账户，密码：admin123');

-- ============================================
-- 常用查询示例
-- ============================================

-- 查询所有启用的用户
-- SELECT * FROM Users WHERE IsEnabled = 1 ORDER BY Role DESC, CreatedAt;

-- 根据卡号查询用户
-- SELECT * FROM Users WHERE CardNumber = 'ADMIN001';

-- 查询某个用户的登录历史
-- SELECT * FROM LoginLogs WHERE UserId = 1 ORDER BY LoginTime DESC;

-- 查询某个用户的权限变更历史
-- SELECT * FROM PermissionChangeLogs WHERE UserId = 1 ORDER BY ChangeTime DESC;

-- 查询最近7天的登录记录
-- SELECT u.UserName, u.RealName, l.LoginTime, l.LogoutTime, l.LoginType
-- FROM LoginLogs l
-- INNER JOIN Users u ON l.UserId = u.Id
-- WHERE l.LoginTime >= DATEADD(DAY, -7, GETDATE())
-- ORDER BY l.LoginTime DESC;

-- ============================================
-- 角色说明
-- ============================================
-- Role = 0: 操作员
--   可访问：主页面、信息统计、报警页面、用户帮助、人员界面（密码登录/指纹登录）
--
-- Role = 1: 工程师
--   可访问：操作员权限 + 配置页面
--
-- Role = 2: 管理员
--   可访问：所有界面（完全权限）
-- ============================================
