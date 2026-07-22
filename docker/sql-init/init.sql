SET NOCOUNT ON;

IF DB_ID(N'OficinaCadastroDb') IS NULL CREATE DATABASE OficinaCadastroDb;
IF DB_ID(N'OficinaEstoqueDb') IS NULL CREATE DATABASE OficinaEstoqueDb;
IF DB_ID(N'OficinaOrdensServicoDb') IS NULL CREATE DATABASE OficinaOrdensServicoDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'oficina_cadastro_app')
    CREATE LOGIN oficina_cadastro_app WITH PASSWORD = '__CADASTRO_PASSWORD__', CHECK_POLICY = OFF;
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'oficina_estoque_app')
    CREATE LOGIN oficina_estoque_app WITH PASSWORD = '__ESTOQUE_PASSWORD__', CHECK_POLICY = OFF;
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'oficina_ordens_app')
    CREATE LOGIN oficina_ordens_app WITH PASSWORD = '__ORDENS_PASSWORD__', CHECK_POLICY = OFF;
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'oficina_e2e_harness')
    CREATE LOGIN oficina_e2e_harness WITH PASSWORD = '__E2E_PASSWORD__', CHECK_POLICY = OFF;
GO

USE OficinaCadastroDb;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'oficina_cadastro_app')
    CREATE USER oficina_cadastro_app FOR LOGIN oficina_cadastro_app;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'oficina_e2e_harness')
    CREATE USER oficina_e2e_harness FOR LOGIN oficina_e2e_harness;
ALTER ROLE db_owner ADD MEMBER oficina_cadastro_app;
ALTER ROLE db_owner ADD MEMBER oficina_e2e_harness;
GO

USE OficinaEstoqueDb;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'oficina_estoque_app')
    CREATE USER oficina_estoque_app FOR LOGIN oficina_estoque_app;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'oficina_e2e_harness')
    CREATE USER oficina_e2e_harness FOR LOGIN oficina_e2e_harness;
ALTER ROLE db_owner ADD MEMBER oficina_estoque_app;
ALTER ROLE db_owner ADD MEMBER oficina_e2e_harness;
GO

USE OficinaOrdensServicoDb;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'oficina_ordens_app')
    CREATE USER oficina_ordens_app FOR LOGIN oficina_ordens_app;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'oficina_e2e_harness')
    CREATE USER oficina_e2e_harness FOR LOGIN oficina_e2e_harness;
ALTER ROLE db_owner ADD MEMBER oficina_ordens_app;
ALTER ROLE db_owner ADD MEMBER oficina_e2e_harness;
GO
