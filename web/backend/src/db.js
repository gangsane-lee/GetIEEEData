require('dotenv').config({ path: require('path').resolve(__dirname, '../.env') });
const sql = require('mssql/msnodesqlv8');

const server   = process.env.DB_SERVER || 'THISMINE';
const database = process.env.DB_NAME   || 'AutoReport';

const config = {
  connectionString:
    `Driver={ODBC Driver 17 for SQL Server};` +
    `Server=${server};` +
    `Database=${database};` +
    `Trusted_Connection=yes;` +
    `Connection Timeout=30;`,
  driver: 'msnodesqlv8',
  pool: { max: 5, min: 1, idleTimeoutMillis: 30000 },
};

let pool = null;

async function getPool() {
  if (!pool) {
    pool = await sql.connect(config);
    console.log('[DB] 연결 성공');
  }
  return pool;
}

module.exports = { getPool, sql };
