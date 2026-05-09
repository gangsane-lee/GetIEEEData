<template>
  <DxPopup
    :visible="visible"
    :drag-enabled="true"
    :close-on-outside-click="true"
    :show-close-button="true"
    :width="680"
    height="auto"
    title="논문 상세"
    @hidden="$emit('update:visible', false)"
  >
    <template #content>
      <div v-if="paper" class="popup-body">
        <!-- 제목 -->
        <h2 class="paper-title">{{ paper.title }}</h2>

        <!-- 메타 정보 -->
        <dl class="paper-meta">
          <div class="meta-row">
            <dt>저자</dt>
            <dd>{{ paper.authors || '정보 없음' }}</dd>
          </div>
          <div class="meta-row">
            <dt>발행일</dt>
            <dd>{{ formatDate(paper.published_date) }}</dd>
          </div>
          <div class="meta-row">
            <dt>출처</dt>
            <dd>{{ paper.site_name || '-' }}</dd>
          </div>
          <div class="meta-row" v-if="paper.keyword">
            <dt>키워드</dt>
            <dd class="keyword-text">{{ paper.keyword }}</dd>
          </div>
          <div class="meta-row" v-if="paper.paper_number">
            <dt>논문 번호</dt>
            <dd>{{ paper.paper_number }}</dd>
          </div>
          <div class="meta-row">
            <dt>적재일시</dt>
            <dd>{{ formatDateTime(paper.loaded_at) }}</dd>
          </div>
        </dl>

        <!-- 원문 링크 버튼 -->
        <div class="popup-actions">
          <DxButton
            v-if="paper.url"
            text="원문 보기"
            type="default"
            styling-mode="contained"
            icon="link"
            @click="openUrl"
          />
          <DxButton
            text="닫기"
            type="normal"
            styling-mode="outlined"
            @click="$emit('update:visible', false)"
          />
        </div>
      </div>
      <div v-else class="popup-loading">
        <DxLoadIndicator :visible="true" />
        <span>불러오는 중...</span>
      </div>
    </template>
  </DxPopup>
</template>

<script setup>
import { DxPopup }        from 'devextreme-vue/popup';
import { DxButton }       from 'devextreme-vue/button';
import { DxLoadIndicator } from 'devextreme-vue/load-indicator';

const props = defineProps({
  visible: { type: Boolean, required: true },
  paper:   { type: Object,  default: null  },
});

defineEmits(['update:visible']);

function openUrl() {
  if (props.paper?.url) {
    window.open(props.paper.url, '_blank', 'noopener,noreferrer');
  }
}

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
.popup-body {
  padding: 4px 8px 16px;
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.paper-title {
  font-size: 17px;
  font-weight: 600;
  color: #1a1a2e;
  line-height: 1.5;
  word-break: break-word;
}

.paper-meta {
  display: flex;
  flex-direction: column;
  gap: 10px;
  border: 1px solid #e8e8e8;
  border-radius: 8px;
  padding: 16px;
  background: #fafafa;
}

.meta-row {
  display: grid;
  grid-template-columns: 90px 1fr;
  gap: 8px;
  align-items: baseline;
}

.meta-row dt {
  font-weight: 600;
  color: #555;
  font-size: 13px;
}

.meta-row dd {
  color: #333;
  font-size: 14px;
  word-break: break-word;
}

.keyword-text {
  color: #1565c0;
  font-size: 13px;
}

.popup-actions {
  display: flex;
  gap: 10px;
  justify-content: flex-end;
}

.popup-loading {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 20px;
  color: #777;
}
</style>
