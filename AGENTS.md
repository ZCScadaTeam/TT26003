# AGENTS.md

## 代码图谱（必读约定）

本项目不使用 Markdown 代码图谱（`docs/CODEMAPS` 已废弃删除，禁止重新生成）。

定位代码统一使用 code-review-graph 知识图谱库：

- 图库位置：`.code-review-graph/graph.db`（SQLite，已 gitignore，不入库）
- 常用命令：
  - `code-review-graph status` — 图统计与新鲜度
  - `code-review-graph search <keyword> --kind Class/Function` — 搜索符号
  - `code-review-graph query callers_of <qualified_name>` — 查调用方
  - `code-review-graph query children_of <qualified_name>` — 查类成员
  - `code-review-graph flows --limit 30 --sort criticality` — 执行流
  - `code-review-graph communities` — 模块社区
  - `code-review-graph large-functions` — 超大文件/函数
- 代码变更后：先 `code-review-graph update` 增量更新图谱，再用图谱定位、分析代码，减少无效阅读。
