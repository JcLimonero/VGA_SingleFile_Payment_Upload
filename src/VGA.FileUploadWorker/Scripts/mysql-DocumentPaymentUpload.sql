-- Seguimiento por intento: detección post-SQLite → proceso local → subida API (Backblaze).
-- Ejecutar en la misma base que documentbyfile (p. ej. single_file).
-- Sin UNIQUE en SourceRelativePath: un mismo path puede generar varias filas en reintentos.

CREATE TABLE IF NOT EXISTS DocumentPaymentUpload (
    Id BIGINT NOT NULL AUTO_INCREMENT
        COMMENT 'PK autonumérica de la fila',
    DiscoveredUtc DATETIME(3) NOT NULL
        COMMENT 'Cuándo se registró el archivo en seguimiento (UTC recomendado)',
    SourceRelativePath VARCHAR(1024) NOT NULL
        COMMENT 'Ruta relativa a la raíz de escaneo; clave estable con el worker y reintentos',
    Channel VARCHAR(128) NOT NULL
        COMMENT 'Carpeta de canal (p. ej. EFECTIVO, TPV, DB)',
    OriginalFileName VARCHAR(512) NOT NULL
        COMMENT 'Nombre del fichero al detectarlo (sin directorio)',
    AgencyAbbreviation VARCHAR(64) NULL
        COMMENT 'Agencia parseada del patrón Agencia_pedido_…',
    OrderNumber VARCHAR(128) NULL
        COMMENT 'Pedido parseado del patrón Agencia_pedido_…',
    IdFile BIGINT NULL
        COMMENT 'IdFile de MySQL (vista / documentbyfile.IdFile)',
    PaymentUploadId BIGINT NULL
        COMMENT 'Id del registro en SQLite PaymentFileUploads',
    DocumentByFileId BIGINT NULL
        COMMENT 'PK Id de la fila en documentbyfile',
    ProcessSucceeded TINYINT(1) NOT NULL DEFAULT 0
        COMMENT '1 = proceso local OK (inserts + archivo en PROCESADOS); 0 = pendiente o falló',
    ProcessedUtc DATETIME(3) NULL
        COMMENT 'Cuándo quedó procesado el archivo',
    ProcessedFullPath VARCHAR(2048) NULL
        COMMENT 'Ruta absoluta del archivo tras mover a PROCESADOS',
    FinalFileName VARCHAR(512) NULL
        COMMENT 'Nombre final en disco (= PathDocument en documentbyfile)',
    CloudUploadAttemptCount INT NOT NULL DEFAULT 0
        COMMENT 'Número de intentos de POST al API (incluye reintentos)',
    CloudLastAttemptUtc DATETIME(3) NULL
        COMMENT 'Último intento de subida al API',
    CloudUploadSucceeded TINYINT(1) NOT NULL DEFAULT 0
        COMMENT '1 = subida al API confirmada OK; 0 = nunca OK o falló el último intento',
    CloudUploadedUtc DATETIME(3) NULL
        COMMENT 'Cuándo se confirmó éxito de subida al API',
    CloudLastError VARCHAR(2000) NULL
        COMMENT 'Último error de subida o fallo de proceso (HTTP / red / cuerpo truncado)',
    UpdatedUtc DATETIME(3) NOT NULL
        COMMENT 'Última modificación de cualquier campo de la fila',
    PRIMARY KEY (Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
COMMENT='Seguimiento: detección → proceso local → subida API';

CREATE INDEX IX_DocumentPaymentUpload_SourcePath
    ON DocumentPaymentUpload (SourceRelativePath(255));

CREATE INDEX IX_DocumentPaymentUpload_DiscoveredUtc
    ON DocumentPaymentUpload (DiscoveredUtc);

CREATE INDEX IX_DocumentPaymentUpload_AgencyOrder
    ON DocumentPaymentUpload (AgencyAbbreviation, OrderNumber);

CREATE INDEX IX_DocumentPaymentUpload_CloudState
    ON DocumentPaymentUpload (ProcessSucceeded, CloudUploadSucceeded, CloudLastAttemptUtc);

CREATE INDEX IX_DocumentPaymentUpload_PaymentUploadId
    ON DocumentPaymentUpload (PaymentUploadId);

CREATE INDEX IX_DocumentPaymentUpload_DocumentByFileId
    ON DocumentPaymentUpload (DocumentByFileId);

CREATE INDEX IX_DocumentPaymentUpload_IdFile
    ON DocumentPaymentUpload (IdFile);
