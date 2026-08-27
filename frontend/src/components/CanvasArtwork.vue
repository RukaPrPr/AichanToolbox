<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ThemeArtwork } from '../theme'

const props = defineProps<{ artwork: Readonly<ThemeArtwork>; active: boolean }>()
const ready = ref(false)
const variables = computed(() => ({
  '--artwork-height': `${(props.artwork.heightRatio ?? .55) * 100}%`,
  '--artwork-min-height': `${props.artwork.minHeight ?? 180}px`,
  '--artwork-max-height': `${props.artwork.maxHeight ?? 380}px`,
  '--artwork-opacity': String(props.artwork.opacity ?? .45),
  '--artwork-active-opacity': String(props.artwork.interactionOpacity ?? .2)
}))
watch(() => props.artwork.src, () => { ready.value = false })
</script>

<template>
  <div
    class="canvas-artwork"
    :data-anchor="artwork.anchor ?? 'bottom-left'"
    :data-active="active"
    :data-ready="ready"
    :style="variables"
    aria-hidden="true"
  >
    <img :key="artwork.src" :src="artwork.src" alt="" draggable="false" @load="ready = true" @error="ready = false" />
  </div>
</template>
