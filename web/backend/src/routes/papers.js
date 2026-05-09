const express = require('express');
const router  = express.Router();
const { getPool, sql } = require('../db');

/**
 * GET /api/papers
 * Query params:
 *   skip     - 건너뛸 행 수 (DevExtreme CustomStore 페이징)
 *   take     - 가져올 행 수
 *   keyword  - keyword 필드 LIKE 검색 (선택)
 *   site     - site_name 필드 LIKE 검색 (선택)
 *   sort     - 정렬 필드명 (선택, 기본: loaded_at)
 *   order    - asc | desc (기본: desc)
 *
 * Response: { data: [...], totalCount: N }
 */
router.get('/', async (req, res) => {
  try {
    const pool    = await getPool();
    const skip    = parseInt(req.query.skip  || '0',  10);
    const take    = parseInt(req.query.take  || '20', 10);
    const keyword = (req.query.keyword || '').trim();
    const site    = (req.query.site    || '').trim();

    // 허용된 정렬 컬럼만 사용 (SQL Injection 방지)
    const allowedSorts = ['loaded_at', 'published_date', 'title', 'site_name', 'journal', 'seq'];
    const sortCol  = allowedSorts.includes(req.query.sort) ? req.query.sort : 'seq';
    const sortDir  = req.query.order === 'asc' ? 'ASC' : 'DESC';

    // WHERE 절 동적 구성
    const conditions = [];
    if (keyword) conditions.push('keyword LIKE @keyword');
    if (site)    conditions.push('site_name LIKE @site');
    const where = conditions.length > 0 ? `WHERE ${conditions.join(' AND ')}` : '';

    // COUNT + 데이터를 단일 request로 처리 (msnodesqlv8 pool 호환)
    const dbReq = pool.request();
    if (keyword) dbReq.input('keyword', sql.NVarChar, `%${keyword}%`);
    if (site)    dbReq.input('site',    sql.NVarChar, `%${site}%`);
    dbReq.input('skip', sql.Int, skip);
    dbReq.input('take', sql.Int, take);

    const dataResult = await dbReq.query(`
      SELECT
        COUNT(*) OVER()        AS totalCount,
        seq,
        url,
        site_name,
        keyword,
        paper_number,
        title,
        LEFT(authors, 300)     AS authors,
        published_date,
        journal,
        extracted_at,
        loaded_at
      FROM [dbo].[pre_table] WITH (NOLOCK)
      ${where}
      ORDER BY ${sortCol} ${sortDir}
      OFFSET @skip ROWS
      FETCH NEXT @take ROWS ONLY
    `);

    const totalCount = dataResult.recordset.length > 0
      ? dataResult.recordset[0].totalCount
      : 0;

    res.json({ data: dataResult.recordset, totalCount });
  } catch (err) {
    console.error('[GET /api/papers]', err);
    res.status(500).json({ error: String(err) });
  }
});

/**
 * GET /api/papers/meta/sites
 * site_name 목록 조회 (필터 드롭다운용)
 * 주의: /:seq 보다 먼저 등록해야 'meta'가 seq로 처리되지 않음
 */
router.get('/meta/sites', async (req, res) => {
  try {
    const pool   = await getPool();
    const result = await pool.request().query(`
      SELECT DISTINCT site_name
      FROM [dbo].[pre_table]
      WHERE site_name IS NOT NULL
      ORDER BY site_name
    `);
    res.json(result.recordset.map(r => r.site_name));
  } catch (err) {
    console.error('[GET /api/papers/meta/sites]', err.message);
    res.status(500).json({ error: err.message });
  }
});

/**
 * GET /api/papers/:seq
 * 논문 단건 상세 조회
 */
router.get('/:seq', async (req, res) => {
  try {
    const pool = await getPool();
    const seq  = parseInt(req.params.seq, 10);

    if (isNaN(seq)) {
      return res.status(400).json({ error: 'seq는 숫자여야 합니다.' });
    }

    const result = await pool.request()
      .input('seq', sql.Int, seq)
      .query(`
        SELECT
          seq, url, site_name, keyword, paper_number,
          title, authors, published_date, journal, extracted_at, loaded_at
        FROM [dbo].[pre_table]
        WHERE seq = @seq
      `);

    if (result.recordset.length === 0) {
      return res.status(404).json({ error: '논문을 찾을 수 없습니다.' });
    }

    res.json(result.recordset[0]);
  } catch (err) {
    console.error('[GET /api/papers/:seq]', err.message);
    res.status(500).json({ error: err.message });
  }
});

module.exports = router;
