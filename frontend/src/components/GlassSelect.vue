<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

interface GlassOption {
  label: string
  value: string | number | boolean
}

const props = defineProps<{
  options: GlassOption[]
  disabled?: boolean
  label: string
}>()
const model = defineModel<any>({ required: true })
const emit = defineEmits<{ (event: 'change', value: string | number | boolean): void }>()
const root = ref<HTMLElement>()
const open = ref(false)

const selectedLabel = computed(() => props.options.find(option => Object.is(option.value, model.value))?.label ?? '')

function toggle() {
  if (!props.disabled) open.value = !open.value
}

function choose(option: GlassOption) {
  model.value = option.value
  open.value = false
  emit('change', option.value)
}

function outsidePointer(event: PointerEvent) {
  if (!root.value?.contains(event.target as Node)) open.value = false
}

function keyDown(event: KeyboardEvent) {
  if (props.disabled) return
  if (event.key === 'Escape') {
    open.value = false
    return
  }
  if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    toggle()
    return
  }
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
  event.preventDefault()
  const current = Math.max(0, props.options.findIndex(option => Object.is(option.value, model.value)))
  const offset = event.key === 'ArrowDown' ? 1 : -1
  const index = Math.min(props.options.length - 1, Math.max(0, current + offset))
  if (props.options[index]) choose(props.options[index])
}

watch(() => props.disabled, disabled => { if (disabled) open.value = false })
onMounted(() => document.addEventListener('pointerdown', outsidePointer, true))
onBeforeUnmount(() => document.removeEventListener('pointerdown', outsidePointer, true))
</script>

<template>
  <div ref="root" class="glass-select" :class="{ open, disabled }" @keydown="keyDown">
    <button
      type="button"
      class="glass-select-trigger"
      role="combobox"
      aria-haspopup="listbox"
      :aria-expanded="open"
      :aria-label="label"
      :disabled="disabled"
      @click="toggle"
    >
      <span>{{ selectedLabel }}</span>
      <svg viewBox="0 0 12 8" aria-hidden="true"><path d="M1.5 1.5 6 6l4.5-4.5" /></svg>
    </button>
    <Transition name="select-pop">
      <div v-if="open" class="glass-select-menu" role="listbox" :aria-label="label">
        <button
          v-for="option in options"
          :key="`${typeof option.value}:${option.value}`"
          type="button"
          role="option"
          :aria-selected="Object.is(option.value, model)"
          :class="{ selected: Object.is(option.value, model) }"
          @pointerdown.prevent
          @click="choose(option)"
        >
          <span>{{ option.label }}</span><i aria-hidden="true">✓</i>
        </button>
      </div>
    </Transition>
  </div>
</template>
