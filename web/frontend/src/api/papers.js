import axios from 'axios';

/**
 * 논문 목록 조회
 * @param {object} params - { skip, take, keyword, site, sort, order }
 * @returns Promise<{ data: Paper[], totalCount: number }>
 */
export function fetchPapers(params) {
  return axios.get('/api/papers', { params }).then((r) => r.data);
}

/**
 * 논문 단건 상세 조회
 * @param {number} seq
 * @returns Promise<Paper>
 */
export function fetchPaper(seq) {
  return axios.get(`/api/papers/${seq}`).then((r) => r.data);
}

/**
 * 출처(site_name) 목록 조회 (필터 드롭다운용)
 * @returns Promise<string[]>
 */
export function fetchSites() {
  return axios.get('/api/papers/meta/sites').then((r) => r.data);
}
