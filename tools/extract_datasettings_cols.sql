SET NOCOUNT ON;
SELECT
    '    ' + QUOTENAME(COLUMN_NAME) + ' ' + DATA_TYPE +
    CASE
        WHEN DATA_TYPE IN ('varchar','nvarchar','char','nchar') THEN
            '(' + CASE WHEN CHARACTER_MAXIMUM_LENGTH = -1 THEN 'MAX' ELSE CAST(CHARACTER_MAXIMUM_LENGTH AS varchar(10)) END + ')'
        WHEN DATA_TYPE IN ('decimal','numeric') THEN
            '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + CAST(NUMERIC_SCALE AS varchar(10)) + ')'
        ELSE ''
    END + ' ' +
    CASE WHEN IS_NULLABLE = 'YES' THEN 'NULL' ELSE 'NOT NULL' END AS line
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'DataSettings'
ORDER BY ORDINAL_POSITION;
