-- Run once against an empty FactoryLoad database, or allow the server to initialize it.
IF OBJECT_ID('dbo.Users') IS NULL
BEGIN
 CREATE TABLE dbo.Users (Id int IDENTITY PRIMARY KEY, Login nvarchar(100) NOT NULL UNIQUE, PasswordHash nvarchar(300) NOT NULL, Role nvarchar(20) NOT NULL CHECK (Role IN ('Admin','Dispatcher')));
 CREATE TABLE dbo.Employees (Id int IDENTITY PRIMARY KEY, Name nvarchar(150) NOT NULL, Position nvarchar(100) NOT NULL);
 CREATE TABLE dbo.Equipment (Id int IDENTITY PRIMARY KEY, Name nvarchar(150) NOT NULL, Model nvarchar(150) NOT NULL);
 CREATE TABLE dbo.Orders (Id int IDENTITY PRIMARY KEY, Number nvarchar(100) NOT NULL UNIQUE, EquipmentId int NOT NULL REFERENCES dbo.Equipment(Id), EmployeeId int NULL REFERENCES dbo.Employees(Id), Description nvarchar(500) NOT NULL, Status nvarchar(20) NOT NULL CHECK (Status IN ('New','InProgress','Done')));
 CREATE TABLE dbo.Events (Id int IDENTITY PRIMARY KEY, EquipmentId int NOT NULL REFERENCES dbo.Equipment(Id), OrderId int NULL REFERENCES dbo.Orders(Id), Status nvarchar(20) NOT NULL CHECK (Status IN ('Work','Idle','Setup')), Reason nvarchar(500) NOT NULL DEFAULT '', StartedAt datetime2 NOT NULL, EndedAt datetime2 NULL, CHECK (EndedAt IS NULL OR EndedAt > StartedAt));
 CREATE UNIQUE INDEX IX_Events_ActiveEquipment ON dbo.Events(EquipmentId) WHERE EndedAt IS NULL;
END
