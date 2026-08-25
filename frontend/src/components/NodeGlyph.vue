<script setup lang="ts">
import type { NodeType } from '../types'

defineProps<{ type: NodeType }>()
</script>

<template>
  <svg class="node-glyph" viewBox="0 0 24 24" aria-hidden="true">
    <template v-if="type === 'ZipExtract'">
      <path d="M5 4h14v5H5zM5 9v11h14V9" /><path d="M12 11v6m-3-3 3 3 3-3" />
    </template>
    <template v-else-if="type === 'Import'">
      <path d="M12 3v11" /><path d="m7.5 10 4.5 4.5 4.5-4.5" /><path d="M5 19h14" />
    </template>
    <template v-else-if="type === 'FormatFilter'">
      <path d="M4 5h16l-6.2 7v4.8l-3.6 2.2v-7Z" />
    </template>
    <template v-else-if="type === 'SizeFilter'">
      <ellipse cx="12" cy="6" rx="7" ry="3" /><path d="M5 6v12c0 1.7 3.1 3 7 3s7-1.3 7-3V6" /><path d="M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3" />
    </template>
    <template v-else-if="type === 'ResolutionFilter'">
      <path d="M8 3H3v5M16 3h5v5M8 21H3v-5M16 21h5v-5" /><rect x="7" y="7" width="10" height="10" rx="1.5" />
    </template>
    <template v-else-if="type === 'ConvertJpg'">
      <path d="M7 3h7l4 4v14H7z" /><path d="M14 3v5h5" /><path d="M10 12v4c0 1.3-.7 2-2 2" />
    </template>
    <template v-else-if="type === 'Resize'">
      <path d="m4 4 6 6M4 9V4h5M20 20l-6-6M20 15v5h-5" />
    </template>
    <template v-else-if="type === 'Descreen'">
      <circle cx="6" cy="7" r="1.5" /><circle cx="12" cy="7" r="1.15" /><circle cx="18" cy="7" r=".75" />
      <circle cx="6" cy="12" r="1.5" /><circle cx="12" cy="12" r="1.15" /><circle cx="18" cy="12" r=".75" />
      <circle cx="6" cy="17" r="1.5" /><circle cx="12" cy="17" r="1.15" /><circle cx="18" cy="17" r=".75" />
    </template>
    <template v-else-if="type === 'Quality'">
      <path d="m12 3 1.8 4.5L18 9.3l-4.2 1.8L12 16l-1.8-4.9L6 9.3l4.2-1.8Z" /><path d="m18.5 15 .8 2.1 2.2.9-2.2.9-.8 2.1-.8-2.1-2.2-.9 2.2-.9Z" />
    </template>
    <template v-else-if="type === 'TargetSize'">
      <circle cx="12" cy="12" r="8" /><circle cx="12" cy="12" r="3" /><path d="M12 2v3m0 14v3M2 12h3m14 0h3" />
    </template>
    <template v-else-if="type === 'ZipPack'">
      <path d="M5 4h14v5H5zM5 9v11h14V9" /><path d="M12 17v-6m-3 3 3-3 3 3" />
    </template>
    <template v-else-if="type === 'DeleteExtracted'">
      <path d="M4 7h16M9 3h6l1 4H8zM7 7l1 14h8l1-14" /><path d="M10 11v6m4-6v6" />
    </template>
    <template v-else>
      <path d="M5 12.5 9.5 17 19 7.5" />
    </template>
  </svg>
</template>
