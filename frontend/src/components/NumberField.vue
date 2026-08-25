<script setup lang="ts">
const props = withDefaults(defineProps<{
  modelValue: number
  min?: number
  max?: number
  step?: number
  disabled?: boolean
  label?: string
}>(), {
  step: 1,
  disabled: false,
  label: '数值'
})

const emit = defineEmits<{
  (event: 'update:modelValue', value: number): void
}>()

function clamp(value: number) {
  if (props.min !== undefined) value = Math.max(props.min, value)
  if (props.max !== undefined) value = Math.min(props.max, value)
  return value
}

function precision(value: number) {
  const decimal = String(value).split('.')[1]
  return decimal?.length ?? 0
}

function stepValue(direction: 1 | -1) {
  if (props.disabled) return
  const digits = Math.min(8, Math.max(precision(props.step), precision(props.modelValue)))
  const factor = 10 ** digits
  const next = (Math.round(props.modelValue * factor) + direction * Math.round(props.step * factor)) / factor
  emit('update:modelValue', clamp(next))
}

function updateValue(event: Event) {
  const value = Number((event.target as HTMLInputElement).value)
  if (Number.isFinite(value)) emit('update:modelValue', value)
}

function normalizeValue(event: Event) {
  const input = event.target as HTMLInputElement
  const value = Number(input.value)
  if (!Number.isFinite(value)) {
    input.value = String(props.modelValue)
    return
  }
  emit('update:modelValue', clamp(value))
}
</script>

<template>
  <div class="number-field">
    <input
      :value="modelValue"
      type="number"
      :min="min"
      :max="max"
      :step="step"
      :disabled="disabled"
      :aria-label="label"
      @input="updateValue"
      @blur="normalizeValue"
    />
    <div class="number-stepper" aria-hidden="true">
      <button type="button" tabindex="-1" :disabled="disabled" @click="stepValue(1)">
        <svg viewBox="0 0 12 8"><path d="M1.25 6.75 6 1.25l4.75 5.5Z" /></svg>
      </button>
      <button type="button" tabindex="-1" :disabled="disabled" @click="stepValue(-1)">
        <svg viewBox="0 0 12 8"><path d="m1.25 1.25 4.75 5.5 4.75-5.5Z" /></svg>
      </button>
    </div>
  </div>
</template>
