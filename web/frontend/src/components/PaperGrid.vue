<template>
  <div class="paper-grid-wrapper">
    <!-- 헤더 -->
    <div class="grid-header">
      <h1 class="grid-title">반도체 논문 저널</h1>
      <p class="grid-subtitle">최신 적재 순서로 정렬됩니다. 논문을 클릭하면 상세 정보를 확인할 수 있습니다.</p>
    </div>

    <!-- 그리드 -->
    <DxDataGrid
      :data-source="dataSource"
      :remote-operations="true"
      :show-borders="true"
      :hover-state-enabled="true"
      :row-alternation-enabled="true"
      :column-auto-width="true"
      :word-wrap-enabled="false"
      :allow-column-resizing="true"
      column-resizing-mode="widget"
      no-data-text="데이터가 없습니다."
      height="calc(100vh - 180px)"
      key-expr="seq"
      @row-click="onRowClick"
    >
      <!-- 컬럼 정의 -->
      <DxColumn
        data-field="seq"
        caption="No."
        :width="70"
        :sort-order="'desc'"
        :sort-index="0"
        :allow-filtering="false"
        :visible="false"
      />
      <DxColumn
        data-field="loaded_at"
        caption="적재일시"
        data-type="datetime"
        :width="155"
        :allow-filtering="false"
        cell-template="dateTimeCell"
      />
      <DxColumn
        data-field="title"
        caption="논문 제목"
        :min-width="300"
      />
      <DxColumn
        data-field="authors"
        caption="저자"
        :width="180"
      />
      <DxColumn
        data-field="published_date"
        caption="발행일"
        data-type="date"
        :width="110"
        :allow-filtering="false"
        cell-template="dateCell"
      />
      <DxColumn
        data-field="site_name"
        caption="출처"
        :width="140"
      />
      <DxColumn
        data-field="keyword"
        caption="키워드"
        :width="200"
      />

      <!-- 셀 커스텀 템플릿 -->
      <template #dateTimeCell="{ data }">
        <span class="date-cell">{{ formatDateTime(data.value) }}</span>
      </template>
      <template #dateCell="{ data }">
        <span>{{ formatDate(data.value) }}</span>
      </template>

      <!-- 페이징 -->
      <DxPaging :page-size="20" />
      <DxPager
        :show-page-size-selector="true"
        :allowed-page-sizes="[10, 20, 50]"
        :show-info="true"
        :show-navigation-buttons="true"
        info-text="{0} / {1} 페이지  (전체 {2}건)"
      />

      <!-- 필터 -->
      <DxFilterRow :visible="true" />
      <DxHeaderFilter :visible="true" />
      <DxSearchPanel :visible="true" placeholder="제목, 저자, 키워드 검색..." :width="240" />

      <!-- 로딩 패널 -->
      <DxLoadPanel :enabled="true" />

      <!-- 스크롤 -->
      <DxScrolling mode="standard" />
    </DxDataGrid>

    <!-- 상세 팝업 -->
    <PaperPopup
      v-model:visible="popupVisible"
      :paper="selectedPaper"
    />
  </div>
</template>

<script setup>
import { ref }           from 'vue';
import CustomStore       from 'devextreme/data/custom_store';

import { DxDataGrid, DxColumn, DxPaging, DxPager,
         DxFilterRow, DxHeaderFilter, DxSearchPanel,
         DxLoadPanel, DxScrolling }
  from 'devextreme-vue/data-grid';

import PaperPopup from './PaperPopup.vue';
import { fetchPapers } from '../api/papers.js';

// ── 팝업 상태 ─────────────────────────────────────────────────
const popupVisible  = ref(false);
const selectedPaper = ref(null);

function onRowClick({ data }) {
  selectedPaper.value = data;
  popupVisible.value  = true;
}

// ── CustomStore — 서버 페이징/필터/정렬 ──────────────────────
const dataSource = new CustomStore({
  key: 'seq',

  async load(loadOptions) {
    const skip    = loadOptions.skip  || 0;
    const take    = loadOptions.take  || 20;

    // 정렬 파싱
    let sort  = 'loaded_at';
    let order = 'desc';
    if (loadOptions.sort?.length) {
      sort  = loadOptions.sort[0].selector;
      order = loadOptions.sort[0].desc ? 'desc' : 'asc';
    }

    // 필터 파싱 (간단 구현: 첫 번째 조건만 처리)
    let keyword = '';
    let site    = '';
    const filter = loadOptions.filter;
    if (filter) {
      extractFilter(filter, { keyword, site }, (k, s) => {
        keyword = k;
        site    = s;
      });
    }

    const result = await fetchPapers({ skip, take, sort, order, keyword, site });
    return {
      data:       result.data,
      totalCount: result.totalCount,
    };
  },
});

/**
 * DxDataGrid 필터 배열을 순회해서 keyword/site_name 값 추출
 * 구조 예: ["keyword", "contains", "TEM"] 또는 중첩 배열
 */
function extractFilter(filter, state, cb) {
  if (!Array.isArray(filter)) return;
  if (typeof filter[0] === 'string') {
    // 단일 조건
    const field = filter[0];
    const value = filter[2] || '';
    if (field === 'keyword')   state.keyword = value;
    if (field === 'site_name') state.site    = value;
    cb(state.keyword, state.site);
  } else {
    // 복합 조건 재귀
    for (const item of filter) {
      if (Array.isArray(item)) extractFilter(item, state, cb);
    }
  }
}

// ── 날짜 포맷 헬퍼 ────────────────────────────────────────────
function formatDate(val) {
  if (!val) return '-';
  return new Date(val).toLocaleDateString('ko-KR', {
    year: 'numeric', month: '2-digit', day: '2-digit',
  });
}

function formatDateTime(val) {
  if (!val) return '-';
  return new Date(val).toLocaleString('ko-KR', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit',
  });
}
</script>

<style scoped>
.paper-grid-wrapper {
  padding: 20px 28px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.grid-header {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.grid-title {
  font-size: 22px;
  font-weight: 700;
  color: #1a1a2e;
}

.grid-subtitle {
  font-size: 13px;
  color: #777;
}

/* 행 hover 커서 */
:deep(.dx-data-row) {
  cursor: pointer;
}

/* 적재일시 컬럼 강조 */
.date-cell {
  color: #1565c0;
  font-weight: 500;
  font-size: 12px;
}
</style>
