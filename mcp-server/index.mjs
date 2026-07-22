#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import Database from "better-sqlite3";
import { z } from "zod";
import path from "path";
import fs from "fs";

const assetRoot = process.argv[2];
if (!assetRoot || !fs.existsSync(assetRoot)) {
  process.stderr.write(`Usage: imageviewer-mcp <asset-folder-path>\n`);
  process.exit(1);
}

const dbPath = path.join(assetRoot, "_db", "assets.db");
if (!fs.existsSync(dbPath)) {
  process.stderr.write(`DB not found: ${dbPath}\nOpen this folder in ImageViewer first.\n`);
  process.exit(1);
}

const db = new Database(dbPath);
db.pragma("journal_mode = WAL");
db.pragma("foreign_keys = ON");
db.pragma("busy_timeout = 5000");

const server = new McpServer({
  name: "imageviewer-asset-manager",
  version: "1.0.0",
});

function getAssetTags(assetId) {
  return db.prepare(`
    SELECT t.name FROM tags t
    JOIN asset_tags at ON at.tag_id = t.id
    WHERE at.asset_id = ?
    ORDER BY t.name
  `).all(assetId).map(r => r.name);
}

function getAssetMeta(assetId) {
  return db.prepare(
    "SELECT asset_type, usage, notes, metadata_json FROM asset_metadata WHERE asset_id = ?"
  ).get(assetId) || null;
}

// --- Tools ---

server.tool(
  "search_assets",
  "アセットを検索。名前、タグ、タイプ、用途で絞り込み可能",
  {
    query: z.string().optional().describe("ファイル名の部分一致検索"),
    tags: z.array(z.string()).optional().describe("タグで絞り込み (AND)"),
    asset_type: z.string().optional().describe("タイプ (texture, sprite, audio_sfx, font, etc.)"),
    usage: z.string().optional().describe("用途 (character, environment, ui, etc.)"),
    extension: z.string().optional().describe("拡張子 (.png, .wav, etc.)"),
    limit: z.number().optional().default(50).describe("最大件数"),
  },
  async ({ query, tags, asset_type, usage, extension, limit }) => {
    let sql = `SELECT a.id, a.path, a.filename, a.extension, a.file_size FROM assets a`;
    const joins = [];
    const wheres = [];
    const params = {};

    if (asset_type || usage) {
      joins.push("LEFT JOIN asset_metadata m ON m.asset_id = a.id");
      if (asset_type) { wheres.push("m.asset_type = @asset_type"); params.asset_type = asset_type; }
      if (usage) { wheres.push("m.usage = @usage"); params.usage = usage; }
    }
    if (query) { wheres.push("a.filename LIKE @query"); params.query = `%${query}%`; }
    if (extension) { wheres.push("a.extension = @ext"); params.ext = extension; }

    if (tags && tags.length > 0) {
      joins.push("JOIN asset_tags at2 ON at2.asset_id = a.id");
      joins.push("JOIN tags t2 ON t2.id = at2.tag_id");
      const tagParams = tags.map((t, i) => { params[`tag${i}`] = t; return `@tag${i}`; });
      wheres.push(`t2.name IN (${tagParams.join(",")})`);
      sql += ` ${joins.join(" ")}`;
      if (wheres.length) sql += ` WHERE ${wheres.join(" AND ")}`;
      sql += ` GROUP BY a.id HAVING COUNT(DISTINCT t2.name) = ${tags.length}`;
    } else {
      if (joins.length) sql += ` ${joins.join(" ")}`;
      if (wheres.length) sql += ` WHERE ${wheres.join(" AND ")}`;
    }

    sql += ` ORDER BY a.path LIMIT @limit`;
    params.limit = limit;

    const rows = db.prepare(sql).all(params);
    const results = rows.map(r => ({
      path: r.path,
      filename: r.filename,
      extension: r.extension,
      file_size: r.file_size,
      full_path: path.join(assetRoot, r.path),
      tags: getAssetTags(r.id),
      metadata: getAssetMeta(r.id),
    }));

    return { content: [{ type: "text", text: JSON.stringify(results, null, 2) }] };
  }
);

server.tool(
  "get_asset_details",
  "指定パスのアセット詳細（タグ、メタデータ、フルパス）を取得",
  {
    asset_path: z.string().describe("アセットの相対パス"),
  },
  async ({ asset_path }) => {
    const row = db.prepare(
      "SELECT id, path, filename, extension, file_size, added_at FROM assets WHERE path = ?"
    ).get(asset_path);
    if (!row) return { content: [{ type: "text", text: `Not found: ${asset_path}` }] };

    const result = {
      ...row,
      full_path: path.join(assetRoot, row.path),
      tags: getAssetTags(row.id),
      metadata: getAssetMeta(row.id),
    };
    return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
  }
);

server.tool(
  "set_metadata",
  "アセットのメタデータ（タイプ、用途、ノート）を設定",
  {
    asset_path: z.string().describe("アセットの相対パス"),
    asset_type: z.string().optional().describe("タイプ: texture, sprite, spritesheet, audio_sfx, audio_bgm, font, ui, vfx, material, animation, tileset"),
    usage: z.string().optional().describe("用途: character, environment, ui, effect, ambient, system"),
    notes: z.string().optional().describe("自由記述メモ"),
  },
  async ({ asset_path, asset_type, usage, notes }) => {
    const row = db.prepare("SELECT id FROM assets WHERE path = ?").get(asset_path);
    if (!row) return { content: [{ type: "text", text: `Not found: ${asset_path}` }] };

    const existing = getAssetMeta(row.id);
    const resolved = {
      asset_type: asset_type !== undefined ? asset_type : (existing?.asset_type ?? ""),
      usage: usage !== undefined ? usage : (existing?.usage ?? ""),
      notes: notes !== undefined ? notes : (existing?.notes ?? ""),
    };
    db.prepare(`
      INSERT INTO asset_metadata (asset_id, asset_type, usage, notes, metadata_json)
      VALUES (?, ?, ?, ?, ?)
      ON CONFLICT(asset_id) DO UPDATE SET
        asset_type = ?, usage = ?, notes = ?
    `).run(
      row.id, resolved.asset_type, resolved.usage, resolved.notes,
      existing?.metadata_json ?? "",
      resolved.asset_type, resolved.usage, resolved.notes
    );

    return { content: [{ type: "text", text: `Updated metadata for ${asset_path}` }] };
  }
);

server.tool(
  "add_tag",
  "アセットにタグを追加",
  {
    asset_path: z.string().describe("アセットの相対パス"),
    tag: z.string().describe("追加するタグ名"),
  },
  async ({ asset_path, tag }) => {
    const row = db.prepare("SELECT id FROM assets WHERE path = ?").get(asset_path);
    if (!row) return { content: [{ type: "text", text: `Not found: ${asset_path}` }] };

    db.prepare("INSERT OR IGNORE INTO tags (name) VALUES (?)").run(tag);
    db.prepare(`
      INSERT OR IGNORE INTO asset_tags (asset_id, tag_id)
      VALUES (?, (SELECT id FROM tags WHERE name = ?))
    `).run(row.id, tag);

    return { content: [{ type: "text", text: `Tagged "${asset_path}" with "${tag}"` }] };
  }
);

server.tool(
  "remove_tag",
  "アセットからタグを削除",
  {
    asset_path: z.string().describe("アセットの相対パス"),
    tag: z.string().describe("削除するタグ名"),
  },
  async ({ asset_path, tag }) => {
    const row = db.prepare("SELECT id FROM assets WHERE path = ?").get(asset_path);
    if (!row) return { content: [{ type: "text", text: `Not found: ${asset_path}` }] };

    db.prepare(`
      DELETE FROM asset_tags WHERE asset_id = ?
      AND tag_id = (SELECT id FROM tags WHERE name = ?)
    `).run(row.id, tag);

    return { content: [{ type: "text", text: `Removed tag "${tag}" from "${asset_path}"` }] };
  }
);

server.tool(
  "list_tags",
  "使用中の全タグ一覧を取得",
  {},
  async () => {
    const tags = db.prepare(`
      SELECT t.name, COUNT(at.asset_id) as count
      FROM tags t JOIN asset_tags at ON at.tag_id = t.id
      GROUP BY t.name ORDER BY t.name
    `).all();
    return { content: [{ type: "text", text: JSON.stringify(tags, null, 2) }] };
  }
);

server.tool(
  "list_asset_types",
  "使用中のアセットタイプと用途の一覧",
  {},
  async () => {
    const types = db.prepare(
      "SELECT DISTINCT asset_type FROM asset_metadata WHERE asset_type != '' ORDER BY asset_type"
    ).all().map(r => r.asset_type);
    const usages = db.prepare(
      "SELECT DISTINCT usage FROM asset_metadata WHERE usage != '' ORDER BY usage"
    ).all().map(r => r.usage);
    return { content: [{ type: "text", text: JSON.stringify({ types, usages }, null, 2) }] };
  }
);

server.tool(
  "get_catalog",
  "全アセットのカタログ（タグ・メタデータ付き）をJSON出力。Godotインポート設定生成等に使用",
  {
    tagged_only: z.boolean().optional().default(true).describe("タグまたはメタデータがあるもののみ"),
  },
  async ({ tagged_only }) => {
    const rows = db.prepare("SELECT id, path, filename, extension, file_size FROM assets ORDER BY path").all();
    const results = rows.map(r => ({
      path: r.path,
      full_path: path.join(assetRoot, r.path),
      extension: r.extension,
      file_size: r.file_size,
      tags: getAssetTags(r.id),
      metadata: getAssetMeta(r.id),
    }));
    const filtered = tagged_only
      ? results.filter(r => r.tags.length > 0 || r.metadata)
      : results;
    return { content: [{ type: "text", text: JSON.stringify(filtered, null, 2) }] };
  }
);

server.tool(
  "bulk_tag",
  "複数アセットに一括タグ付け（パスのパターンマッチ）",
  {
    pattern: z.string().describe("パスのパターン（部分一致）。例: 'textures/character'"),
    tag: z.string().describe("付与するタグ"),
  },
  async ({ pattern, tag }) => {
    db.prepare("INSERT OR IGNORE INTO tags (name) VALUES (?)").run(tag);
    const rows = db.prepare("SELECT id, path FROM assets WHERE path LIKE ?").all(`%${pattern}%`);
    let count = 0;
    const stmt = db.prepare(`
      INSERT OR IGNORE INTO asset_tags (asset_id, tag_id)
      VALUES (?, (SELECT id FROM tags WHERE name = ?))
    `);
    for (const row of rows) {
      const changes = stmt.run(row.id, tag).changes;
      count += changes;
    }
    return { content: [{ type: "text", text: `Tagged ${count} assets matching "${pattern}" with "${tag}" (${rows.length} matched)` }] };
  }
);

server.tool(
  "stats",
  "アセットDB の統計情報",
  {},
  async () => {
    const total = db.prepare("SELECT COUNT(*) as c FROM assets").get().c;
    const tagged = db.prepare("SELECT COUNT(DISTINCT asset_id) as c FROM asset_tags").get().c;
    const withMeta = db.prepare("SELECT COUNT(*) as c FROM asset_metadata").get().c;
    const byExt = db.prepare(
      "SELECT extension, COUNT(*) as count FROM assets GROUP BY extension ORDER BY count DESC"
    ).all();
    return {
      content: [{
        type: "text",
        text: JSON.stringify({ total, tagged, with_metadata: withMeta, by_extension: byExt, root: assetRoot }, null, 2)
      }]
    };
  }
);

// --- Start ---
const transport = new StdioServerTransport();
await server.connect(transport);
