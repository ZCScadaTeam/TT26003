-- ============================================
-- 用户权限管理 - 最小化建表脚本
-- 只包含Users表，用于权限修改功能
-- ============================================

-- 创建数据库（如果不存在）
IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'DTDJJ_DB')
BEGIN
    CREATE DATABASE DTDJJ_DB;
END
GO

USE DTDJJ_DB;
GO

-- 创建用户表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
BEGIN
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
    
    PRINT '用户表创建成功';
END
ELSE
BEGIN
    PRINT '用户表已存在';
END
GO

-- 插入默认测试用户（如果表为空）
IF NOT EXISTS (SELECT * FROM Users)
BEGIN
    INSERT INTO Users (UserName, RealName, FingerprintId, Password, Role, IsEnabled, Remark)
    VALUES 
        ('admin', '系统管理员', 'ADMIN001', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 2, 1, '默认管理员账户，密码：admin123'),
        ('engineer', '工程师', 'ENG001', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 1, 1, '默认工程师账户，密码：admin123'),
        ('operator', '操作员', 'OPR001', 'jGl25bVBBBW96Qi9Te4V37Fnqchz/Eu4qB9vKrRIqRg=', 0, 1, '默认操作员账户，密码：admin123');
    
    PRINT '默认用户数据插入成功';
END
ELSE
BEGIN
    PRINT '用户数据已存在';
END
GO

-- 验证数据
SELECT 
    Id,
    UserName,
    RealName,
    FingerprintId,
    CASE Role 
        WHEN 0 THEN '操作员'
        WHEN 1 THEN '工程师'
        WHEN 2 THEN '管理员'
        ELSE '未知'
    END AS RoleName,
    IsEnabled,
    CreatedAt
FROM Users
ORDER BY Role DESC, Id;
GO

PRINT '========================================';
PRINT '用户表创建完成！';
PRINT '默认账户：';
PRINT '  admin / admin123 (管理员)';
PRINT '  engineer / admin123 (工程师)';
PRINT '  operator / admin123 (操作员)';
PRINT '========================================';
