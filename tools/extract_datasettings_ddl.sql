SET NOCOUNT ON;
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + CASE WHEN @sql = N'' THEN N'' ELSE N',' + CHAR(13) END +
    N'    ' + QUOTENAME(COLUMN_NAME) + N' ' + CAST(DATA_TYPE AS nvarchar(max)) +
    CASE
        WHEN DATA_TYPE IN ('varchar','nvarchar','char','nchar') THEN
            N'(' + CASE WHEN CHARACTER_MAXIMUM_LENGTH = -1 THEN N'MAX' ELSE CAST(CHARACTER_MAXIMUM_LENGTH AS nvarchar(10)) END + N')'
        WHEN DATA_TYPE IN ('decimal','numeric') THEN
            N'(' + CAST(NUMERIC_PRECISION AS nvarchar(10)) + N',' + CAST(NUMERIC_SCALE AS nvarchar(10)) + N')'
        ELSE N''
    END + N' ' +
    CASE WHEN IS_NULLABLE = 'YES' THEN N'NULL' ELSE N'NOT NULL' END
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'DataSettings'
ORDER BY ORDINAL_POSITION;

SELECT N'CREATE TABLE dbo.DataSettings (' + CHAR(13) + @sql + CHAR(13) + N');' AS ddl;
