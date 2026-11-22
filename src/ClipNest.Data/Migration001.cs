namespace ClipNest.Data;

public static class Migration001
{
    public const string Sql = """
        CREATE TABLE folders (
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE COLLATE NOCASE,
          watch_enabled INTEGER NOT NULL DEFAULT 1,
          last_scan_at TEXT,
          created_at TEXT NOT NULL
        );

        CREATE TABLE assets (
          id INTEGER PRIMARY KEY,
          sha256 TEXT NOT NULL UNIQUE,
          file_name TEXT NOT NULL,
          width INTEGER,
          height INTEGER,
          orientation INTEGER,
          taken_at TEXT,
          camera_make TEXT,
          camera_model TEXT,
          lens TEXT,
          iso INTEGER,
          aperture REAL,
          shutter TEXT,
          focal_length REAL,
          rating INTEGER NOT NULL DEFAULT 0,
          is_favorite INTEGER NOT NULL DEFAULT 0,
          flag INTEGER NOT NULL DEFAULT 0,
          decode_status TEXT NOT NULL DEFAULT 'pending',
          embedding_status TEXT NOT NULL DEFAULT 'pending',
          notes TEXT NOT NULL DEFAULT '',
          ocr_text TEXT NOT NULL DEFAULT '',
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );

        CREATE TABLE asset_files (
          id INTEGER PRIMARY KEY,
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          folder_id INTEGER NOT NULL REFERENCES folders(id),
          path TEXT NOT NULL UNIQUE COLLATE NOCASE,
          file_name TEXT NOT NULL,
          extension TEXT NOT NULL,
          size_bytes INTEGER NOT NULL,
          modified_at TEXT NOT NULL,
          is_primary INTEGER NOT NULL DEFAULT 0,
          file_state TEXT NOT NULL DEFAULT 'indexed',
          error_message TEXT,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );

        CREATE INDEX ix_asset_files_asset ON asset_files(asset_id);
        CREATE UNIQUE INDEX ux_asset_files_primary ON asset_files(asset_id) WHERE is_primary = 1;
        CREATE INDEX ix_assets_taken_at ON assets(taken_at);
        CREATE INDEX ix_assets_decode_status ON assets(decode_status);
        CREATE INDEX ix_assets_embedding_status ON assets(embedding_status);

        CREATE TABLE tags (
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          normalized_name TEXT NOT NULL,
          parent_id INTEGER REFERENCES tags(id),
          color TEXT,
          created_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_tags_parent_name ON tags(COALESCE(parent_id, 0), normalized_name);

        CREATE TABLE tag_aliases (
          tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
          alias TEXT NOT NULL,
          normalized_alias TEXT NOT NULL,
          PRIMARY KEY(tag_id, normalized_alias)
        );

        CREATE TABLE asset_tags (
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
          source TEXT NOT NULL DEFAULT 'manual',
          confidence REAL,
          PRIMARY KEY(asset_id, tag_id)
        );
        CREATE INDEX ix_asset_tags_tag ON asset_tags(tag_id, asset_id);

        CREATE TABLE albums (
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          created_at TEXT NOT NULL
        );

        CREATE TABLE album_assets (
          album_id INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          sort_order INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(album_id, asset_id)
        );

        CREATE TABLE saved_searches (
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          query TEXT NOT NULL,
          created_at TEXT NOT NULL
        );

        CREATE TABLE thumbnails (
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          size INTEGER NOT NULL,
          cache_path TEXT NOT NULL,
          width INTEGER NOT NULL,
          height INTEGER NOT NULL,
          source TEXT NOT NULL,
          created_at TEXT NOT NULL,
          PRIMARY KEY(asset_id, size)
        );

        CREATE TABLE galaxy_clusters (
          id INTEGER PRIMARY KEY,
          tag_id INTEGER REFERENCES tags(id),
          label TEXT NOT NULL,
          center_x REAL NOT NULL,
          center_y REAL NOT NULL,
          radius REAL NOT NULL,
          layout_version INTEGER NOT NULL
        );

        CREATE TABLE galaxy_nodes (
          asset_id INTEGER PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
          cluster_id INTEGER NOT NULL REFERENCES galaxy_clusters(id),
          x REAL NOT NULL,
          y REAL NOT NULL,
          semantic_x REAL,
          semantic_y REAL,
          layout_version INTEGER NOT NULL
        );
        CREATE INDEX ix_galaxy_nodes_cluster ON galaxy_nodes(cluster_id);

        CREATE TABLE asset_embeddings (
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          model_id TEXT NOT NULL,
          dim INTEGER NOT NULL,
          vector BLOB NOT NULL,
          created_at TEXT NOT NULL,
          PRIMARY KEY(asset_id, model_id)
        );

        CREATE TABLE models (
          id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          dim INTEGER NOT NULL,
          file_path TEXT,
          sha256 TEXT,
          license TEXT,
          status TEXT NOT NULL DEFAULT 'missing'
        );

        CREATE VIRTUAL TABLE asset_fts USING fts5(
          file_name,
          notes,
          ocr_text,
          content='assets',
          content_rowid='id',
          tokenize='trigram'
        );

        CREATE TRIGGER assets_ai AFTER INSERT ON assets BEGIN
          INSERT INTO asset_fts(rowid, file_name, notes, ocr_text)
          VALUES (new.id, new.file_name, new.notes, new.ocr_text);
        END;

        CREATE TRIGGER assets_ad AFTER DELETE ON assets BEGIN
          INSERT INTO asset_fts(asset_fts, rowid, file_name, notes, ocr_text)
          VALUES ('delete', old.id, old.file_name, old.notes, old.ocr_text);
        END;

        CREATE TRIGGER assets_au AFTER UPDATE OF file_name, notes, ocr_text ON assets BEGIN
          INSERT INTO asset_fts(asset_fts, rowid, file_name, notes, ocr_text)
          VALUES ('delete', old.id, old.file_name, old.notes, old.ocr_text);
          INSERT INTO asset_fts(rowid, file_name, notes, ocr_text)
          VALUES (new.id, new.file_name, new.notes, new.ocr_text);
        END;
        """;
}
