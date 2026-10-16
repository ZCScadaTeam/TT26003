-- ============================================================
-- DXX 建库脚本 (PostgreSQL)
-- 必须【单独执行】，不能和建表语句放在同一个事务/脚本里，
-- 因为 CREATE DATABASE 不能在事务块内运行。
-- ============================================================

CREATE DATABASE "DXX"
    WITH OWNER = postgres
         ENCODING = 'UTF8'
         LC_COLLATE = 'C'
         LC_CTYPE = 'C'
         TEMPLATE = template0;
