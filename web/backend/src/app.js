require('dotenv').config({ path: require('path').resolve(__dirname, '../.env') });
const express    = require('express');
const cors       = require('cors');
const papersRoute = require('./routes/papers');

const app  = express();
const PORT = process.env.PORT || 3000;

app.use(cors());
app.use(express.json());

// 라우터
app.use('/api/papers', papersRoute);

// 헬스 체크
app.get('/api/health', (_, res) => res.json({ status: 'ok', time: new Date() }));

app.listen(PORT, () => {
  console.log(`[서버] http://localhost:${PORT} 에서 실행 중`);
});
