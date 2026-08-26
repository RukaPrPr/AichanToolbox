export type ConnectionPoint = { x: number; y: number }

export type RenderConnection = {
  start: ConnectionPoint
  end: ConnectionPoint
  color: string
  active: boolean
  dimmed: boolean
}

export type ConnectionViewport = {
  x: number
  y: number
  zoom: number
}

type CurveSnapshot = {
  startX: number
  startY: number
  endX: number
  endY: number
  color: string
  state: number
}

type DrawPass = {
  widths: readonly [number, number, number]
  alphas: readonly [number, number, number]
  innerEdge: number
  whiteMix: number
}

type ProgramBindings = {
  resolution: WebGLUniformLocation | null
  pan: WebGLUniformLocation | null
  zoom: WebGLUniformLocation | null
  ratio: WebGLUniformLocation | null
  widths: WebGLUniformLocation | null
  alphas: WebGLUniformLocation | null
  innerEdge: WebGLUniformLocation | null
  whiteMix: WebGLUniformLocation | null
}

const lineVertexShader = `
attribute vec2 a_center;
attribute vec2 a_normal;
attribute float a_side;
attribute vec3 a_color;
attribute float a_state;
uniform vec2 u_resolution;
uniform vec2 u_pan;
uniform float u_zoom;
uniform float u_ratio;
uniform vec3 u_widths;
uniform vec3 u_alphas;
uniform float u_white_mix;
varying float v_edge;
varying vec4 v_color;

float selectValue(vec3 values, float state) {
  return state < 0.5 ? values.x : (state < 1.5 ? values.y : values.z);
}

void main() {
  float width = selectValue(u_widths, a_state) * u_zoom * u_ratio;
  vec2 center = (a_center * u_zoom + u_pan) * u_ratio;
  vec2 position = center + a_normal * a_side * width * 0.5;
  vec2 clip = (position / u_resolution) * 2.0 - 1.0;
  gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
  v_edge = a_side;
  v_color = vec4(mix(a_color, vec3(1.0), u_white_mix), selectValue(u_alphas, a_state));
}
`

const lineFragmentShader = `
precision mediump float;
varying float v_edge;
varying vec4 v_color;
uniform float u_inner_edge;

void main() {
  float coverage = 1.0 - smoothstep(u_inner_edge, 1.0, abs(v_edge));
  float alpha = v_color.a * coverage;
  gl_FragColor = vec4(v_color.rgb * alpha, alpha);
}
`

const pointVertexShader = `
attribute vec2 a_center;
attribute vec3 a_color;
attribute float a_state;
uniform vec2 u_resolution;
uniform vec2 u_pan;
uniform float u_zoom;
uniform float u_ratio;
uniform vec3 u_widths;
uniform vec3 u_alphas;
uniform float u_white_mix;
varying vec4 v_color;

float selectValue(vec3 values, float state) {
  return state < 0.5 ? values.x : (state < 1.5 ? values.y : values.z);
}

void main() {
  vec2 position = (a_center * u_zoom + u_pan) * u_ratio;
  vec2 clip = (position / u_resolution) * 2.0 - 1.0;
  gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
  gl_PointSize = selectValue(u_widths, a_state) * u_zoom * u_ratio;
  v_color = vec4(mix(a_color, vec3(1.0), u_white_mix), selectValue(u_alphas, a_state));
}
`

const pointFragmentShader = `
precision mediump float;
varying vec4 v_color;
uniform float u_inner_edge;

void main() {
  float radius = length(gl_PointCoord - vec2(0.5)) * 2.0;
  float coverage = 1.0 - smoothstep(u_inner_edge, 1.0, radius);
  float alpha = v_color.a * coverage;
  gl_FragColor = vec4(v_color.rgb * alpha, alpha);
}
`

const drawPasses: readonly DrawPass[] = [
  { widths: [10, 22, 5], alphas: [.075, .16, .025], innerEdge: 0, whiteMix: 0 },
  { widths: [6, 12, 3], alphas: [.13, .27, .04], innerEdge: .08, whiteMix: 0 },
  { widths: [2.15, 3.5, 1.5], alphas: [.74, .94, .18], innerEdge: .68, whiteMix: 0 },
  { widths: [.65, .65, .65], alphas: [.34, .58, .08], innerEdge: .45, whiteMix: 1 }
]

class FloatBuilder {
  private storage: Float32Array
  length = 0

  constructor(capacity: number) {
    this.storage = new Float32Array(capacity)
  }

  reset() {
    this.length = 0
  }

  line(centerX: number, centerY: number, normalX: number, normalY: number, side: number, red: number, green: number, blue: number, state: number) {
    this.ensure(9)
    const target = this.storage
    let offset = this.length
    target[offset++] = centerX
    target[offset++] = centerY
    target[offset++] = normalX
    target[offset++] = normalY
    target[offset++] = side
    target[offset++] = red
    target[offset++] = green
    target[offset++] = blue
    target[offset++] = state
    this.length = offset
  }

  point(centerX: number, centerY: number, red: number, green: number, blue: number, state: number) {
    this.ensure(6)
    const target = this.storage
    let offset = this.length
    target[offset++] = centerX
    target[offset++] = centerY
    target[offset++] = red
    target[offset++] = green
    target[offset++] = blue
    target[offset++] = state
    this.length = offset
  }

  view() {
    return this.storage.subarray(0, this.length)
  }

  private ensure(additional: number) {
    if (this.length + additional <= this.storage.length) return
    let capacity = this.storage.length
    while (capacity < this.length + additional) capacity *= 2
    const expanded = new Float32Array(capacity)
    expanded.set(this.storage)
    this.storage = expanded
  }
}

function compileShader(gl: WebGLRenderingContext, type: number, source: string) {
  const shader = gl.createShader(type)
  if (!shader) throw new Error('无法创建 WebGL 着色器。')
  gl.shaderSource(shader, source)
  gl.compileShader(shader)
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const message = gl.getShaderInfoLog(shader) ?? '未知着色器错误'
    gl.deleteShader(shader)
    throw new Error(message)
  }
  return shader
}

function createProgram(gl: WebGLRenderingContext, vertexSource: string, fragmentSource: string) {
  const vertex = compileShader(gl, gl.VERTEX_SHADER, vertexSource)
  const fragment = compileShader(gl, gl.FRAGMENT_SHADER, fragmentSource)
  const program = gl.createProgram()
  if (!program) throw new Error('无法创建 WebGL 程序。')
  gl.attachShader(program, vertex)
  gl.attachShader(program, fragment)
  gl.linkProgram(program)
  gl.deleteShader(vertex)
  gl.deleteShader(fragment)
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    const message = gl.getProgramInfoLog(program) ?? '未知 WebGL 链接错误'
    gl.deleteProgram(program)
    throw new Error(message)
  }
  return program
}

function bindings(gl: WebGLRenderingContext, program: WebGLProgram): ProgramBindings {
  return {
    resolution: gl.getUniformLocation(program, 'u_resolution'),
    pan: gl.getUniformLocation(program, 'u_pan'),
    zoom: gl.getUniformLocation(program, 'u_zoom'),
    ratio: gl.getUniformLocation(program, 'u_ratio'),
    widths: gl.getUniformLocation(program, 'u_widths'),
    alphas: gl.getUniformLocation(program, 'u_alphas'),
    innerEdge: gl.getUniformLocation(program, 'u_inner_edge'),
    whiteMix: gl.getUniformLocation(program, 'u_white_mix')
  }
}

function parseColor(value: string): readonly [number, number, number] {
  const normalized = value.trim().replace(/^#/, '')
  const hex = normalized.length === 3
    ? normalized.split('').map(part => `${part}${part}`).join('')
    : normalized.padEnd(6, '0').slice(0, 6)
  return [
    Number.parseInt(hex.slice(0, 2), 16) / 255,
    Number.parseInt(hex.slice(2, 4), 16) / 255,
    Number.parseInt(hex.slice(4, 6), 16) / 255
  ]
}

function controlDistance(start: ConnectionPoint, end: ConnectionPoint) {
  const horizontal = Math.abs(end.x - start.x)
  const vertical = Math.abs(end.y - start.y)
  const verticalAssist = Math.min(72, vertical * .2)
  return Math.max(horizontal * .48, verticalAssist)
}

function cubicPoint(start: ConnectionPoint, controlA: ConnectionPoint, controlB: ConnectionPoint, end: ConnectionPoint, t: number) {
  const inverse = 1 - t
  const inverse2 = inverse * inverse
  const t2 = t * t
  return {
    x: inverse2 * inverse * start.x + 3 * inverse2 * t * controlA.x + 3 * inverse * t2 * controlB.x + t2 * t * end.x,
    y: inverse2 * inverse * start.y + 3 * inverse2 * t * controlA.y + 3 * inverse * t2 * controlB.y + t2 * t * end.y
  }
}

function cubicTangent(start: ConnectionPoint, controlA: ConnectionPoint, controlB: ConnectionPoint, end: ConnectionPoint, t: number) {
  const inverse = 1 - t
  return {
    x: 3 * inverse * inverse * (controlA.x - start.x) + 6 * inverse * t * (controlB.x - controlA.x) + 3 * t * t * (end.x - controlB.x),
    y: 3 * inverse * inverse * (controlA.y - start.y) + 6 * inverse * t * (controlB.y - controlA.y) + 3 * t * t * (end.y - controlB.y)
  }
}

function curveState(curve: RenderConnection) {
  return curve.active ? 1 : curve.dimmed ? 2 : 0
}

/**
 * Fixed-viewport GPU connection layer. Curve geometry is uploaded only when a
 * port or route changes; pan and zoom redraw every connection by changing only
 * uniforms. DOM nodes remain normal HTML and keep their full glass appearance.
 */
export class WebGlConnectionRenderer {
  readonly backend: 'webgl' | 'canvas2d'

  private readonly canvas: HTMLCanvasElement
  private readonly gl: WebGLRenderingContext | null
  private readonly fallback: CanvasRenderingContext2D | null
  private lineProgram: WebGLProgram | null = null
  private pointProgram: WebGLProgram | null = null
  private lineBindings: ProgramBindings | null = null
  private pointBindings: ProgramBindings | null = null
  private lineBuffer: WebGLBuffer | null = null
  private pointBuffer: WebGLBuffer | null = null
  private lineVertexCount = 0
  private pointVertexCount = 0
  private pixelWidth = 0
  private pixelHeight = 0
  private ratio = 1
  private snapshots: CurveSnapshot[] = []
  private readonly lineBuilder = new FloatBuilder(16_384)
  private readonly pointBuilder = new FloatBuilder(512)

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas
    this.gl = canvas.getContext('webgl', {
      alpha: true,
      antialias: true,
      depth: false,
      stencil: false,
      premultipliedAlpha: true,
      preserveDrawingBuffer: false,
      powerPreference: 'high-performance'
    })
    this.fallback = this.gl ? null : canvas.getContext('2d')
    this.backend = this.gl ? 'webgl' : 'canvas2d'
    canvas.dataset.connectionRenderer = this.backend
    if (this.gl) this.initializeWebGl(this.gl)
  }

  resize(width: number, height: number, ratio = window.devicePixelRatio || 1) {
    this.ratio = Math.max(1, ratio)
    const pixelWidth = Math.max(1, Math.round(width * this.ratio))
    const pixelHeight = Math.max(1, Math.round(height * this.ratio))
    if (pixelWidth === this.pixelWidth && pixelHeight === this.pixelHeight) return
    this.pixelWidth = pixelWidth
    this.pixelHeight = pixelHeight
    this.canvas.width = pixelWidth
    this.canvas.height = pixelHeight
    this.canvas.style.width = `${Math.max(1, width)}px`
    this.canvas.style.height = `${Math.max(1, height)}px`
    this.gl?.viewport(0, 0, pixelWidth, pixelHeight)
  }

  render(curves: RenderConnection[], viewport: ConnectionViewport) {
    if (this.gl) this.renderWebGl(this.gl, curves, viewport)
    else if (this.fallback) this.renderCanvas(this.fallback, curves, viewport)
  }

  dispose() {
    const gl = this.gl
    if (!gl) return
    if (this.lineBuffer) gl.deleteBuffer(this.lineBuffer)
    if (this.pointBuffer) gl.deleteBuffer(this.pointBuffer)
    if (this.lineProgram) gl.deleteProgram(this.lineProgram)
    if (this.pointProgram) gl.deleteProgram(this.pointProgram)
  }

  private initializeWebGl(gl: WebGLRenderingContext) {
    this.lineProgram = createProgram(gl, lineVertexShader, lineFragmentShader)
    this.pointProgram = createProgram(gl, pointVertexShader, pointFragmentShader)
    this.lineBindings = bindings(gl, this.lineProgram)
    this.pointBindings = bindings(gl, this.pointProgram)
    this.lineBuffer = gl.createBuffer()
    this.pointBuffer = gl.createBuffer()
    gl.disable(gl.DEPTH_TEST)
    gl.disable(gl.CULL_FACE)
    gl.enable(gl.BLEND)
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA)
    gl.clearColor(0, 0, 0, 0)
  }

  private renderWebGl(gl: WebGLRenderingContext, curves: RenderConnection[], viewport: ConnectionViewport) {
    gl.viewport(0, 0, this.pixelWidth, this.pixelHeight)
    gl.clear(gl.COLOR_BUFFER_BIT)
    if (!this.lineProgram || !this.pointProgram || !this.lineBindings || !this.pointBindings || !this.lineBuffer || !this.pointBuffer) return
    if (!this.geometryMatches(curves)) this.uploadGeometry(gl, curves)
    if (!this.lineVertexCount) return

    this.prepareLineProgram(gl, viewport)
    for (const pass of drawPasses) {
      this.applyPass(gl, this.lineBindings, pass)
      gl.drawArrays(gl.TRIANGLES, 0, this.lineVertexCount)
    }

    this.preparePointProgram(gl, viewport)
    for (const pass of drawPasses) {
      this.applyPass(gl, this.pointBindings, pass)
      gl.drawArrays(gl.POINTS, 0, this.pointVertexCount)
    }
  }

  private geometryMatches(curves: RenderConnection[]) {
    if (curves.length !== this.snapshots.length) return false
    for (let index = 0; index < curves.length; index += 1) {
      const curve = curves[index]
      const snapshot = this.snapshots[index]
      if (curve.start.x !== snapshot.startX || curve.start.y !== snapshot.startY
        || curve.end.x !== snapshot.endX || curve.end.y !== snapshot.endY
        || curve.color !== snapshot.color || curveState(curve) !== snapshot.state) return false
    }
    return true
  }

  private uploadGeometry(gl: WebGLRenderingContext, curves: RenderConnection[]) {
    this.lineBuilder.reset()
    this.pointBuilder.reset()
    this.snapshots = curves.map(curve => ({
      startX: curve.start.x,
      startY: curve.start.y,
      endX: curve.end.x,
      endY: curve.end.y,
      color: curve.color,
      state: curveState(curve)
    }))

    const ordered = [...curves.filter(curve => !curve.active), ...curves.filter(curve => curve.active)]
    for (const curve of ordered) this.appendCurve(curve)

    gl.bindBuffer(gl.ARRAY_BUFFER, this.lineBuffer)
    gl.bufferData(gl.ARRAY_BUFFER, this.lineBuilder.view(), gl.DYNAMIC_DRAW)
    gl.bindBuffer(gl.ARRAY_BUFFER, this.pointBuffer)
    gl.bufferData(gl.ARRAY_BUFFER, this.pointBuilder.view(), gl.DYNAMIC_DRAW)
    this.lineVertexCount = this.lineBuilder.length / 9
    this.pointVertexCount = this.pointBuilder.length / 6
  }

  private appendCurve(curve: RenderConnection) {
    const distance = controlDistance(curve.start, curve.end)
    const controlA = { x: curve.start.x + distance, y: curve.start.y }
    const controlB = { x: curve.end.x - distance, y: curve.end.y }
    const estimatedLength = Math.hypot(curve.end.x - curve.start.x, curve.end.y - curve.start.y) + Math.abs(controlA.x - controlB.x) * .45
    const segments = Math.max(18, Math.min(128, Math.ceil(estimatedLength / 14)))
    const [red, green, blue] = parseColor(curve.color)
    const state = curveState(curve)
    let previousCenter = curve.start
    let previousNormal = { x: 0, y: 1 }

    this.pointBuilder.point(curve.start.x, curve.start.y, red, green, blue, state)
    this.pointBuilder.point(curve.end.x, curve.end.y, red, green, blue, state)

    for (let index = 0; index <= segments; index += 1) {
      const t = index / segments
      const center = cubicPoint(curve.start, controlA, controlB, curve.end, t)
      const tangent = cubicTangent(curve.start, controlA, controlB, curve.end, t)
      let length = Math.hypot(tangent.x, tangent.y)
      if (length < .0001) {
        tangent.x = curve.end.x - curve.start.x
        tangent.y = curve.end.y - curve.start.y
        length = Math.max(.0001, Math.hypot(tangent.x, tangent.y))
      }
      const normal = { x: -tangent.y / length, y: tangent.x / length }
      if (index > 0) {
        this.lineBuilder.line(previousCenter.x, previousCenter.y, previousNormal.x, previousNormal.y, -1, red, green, blue, state)
        this.lineBuilder.line(previousCenter.x, previousCenter.y, previousNormal.x, previousNormal.y, 1, red, green, blue, state)
        this.lineBuilder.line(center.x, center.y, normal.x, normal.y, -1, red, green, blue, state)
        this.lineBuilder.line(previousCenter.x, previousCenter.y, previousNormal.x, previousNormal.y, 1, red, green, blue, state)
        this.lineBuilder.line(center.x, center.y, normal.x, normal.y, 1, red, green, blue, state)
        this.lineBuilder.line(center.x, center.y, normal.x, normal.y, -1, red, green, blue, state)
      }
      previousCenter = center
      previousNormal = normal
    }
  }

  private prepareLineProgram(gl: WebGLRenderingContext, viewport: ConnectionViewport) {
    if (!this.lineProgram || !this.lineBindings || !this.lineBuffer) return
    gl.useProgram(this.lineProgram)
    gl.bindBuffer(gl.ARRAY_BUFFER, this.lineBuffer)
    const stride = 9 * Float32Array.BYTES_PER_ELEMENT
    this.enableAttribute(gl, this.lineProgram, 'a_center', 2, stride, 0)
    this.enableAttribute(gl, this.lineProgram, 'a_normal', 2, stride, 2 * Float32Array.BYTES_PER_ELEMENT)
    this.enableAttribute(gl, this.lineProgram, 'a_side', 1, stride, 4 * Float32Array.BYTES_PER_ELEMENT)
    this.enableAttribute(gl, this.lineProgram, 'a_color', 3, stride, 5 * Float32Array.BYTES_PER_ELEMENT)
    this.enableAttribute(gl, this.lineProgram, 'a_state', 1, stride, 8 * Float32Array.BYTES_PER_ELEMENT)
    this.applyViewportUniforms(gl, this.lineBindings, viewport)
  }

  private preparePointProgram(gl: WebGLRenderingContext, viewport: ConnectionViewport) {
    if (!this.pointProgram || !this.pointBindings || !this.pointBuffer) return
    gl.useProgram(this.pointProgram)
    gl.bindBuffer(gl.ARRAY_BUFFER, this.pointBuffer)
    const stride = 6 * Float32Array.BYTES_PER_ELEMENT
    this.enableAttribute(gl, this.pointProgram, 'a_center', 2, stride, 0)
    this.enableAttribute(gl, this.pointProgram, 'a_color', 3, stride, 2 * Float32Array.BYTES_PER_ELEMENT)
    this.enableAttribute(gl, this.pointProgram, 'a_state', 1, stride, 5 * Float32Array.BYTES_PER_ELEMENT)
    this.applyViewportUniforms(gl, this.pointBindings, viewport)
  }

  private applyViewportUniforms(gl: WebGLRenderingContext, target: ProgramBindings, viewport: ConnectionViewport) {
    gl.uniform2f(target.resolution, this.pixelWidth, this.pixelHeight)
    gl.uniform2f(target.pan, viewport.x, viewport.y)
    gl.uniform1f(target.zoom, viewport.zoom)
    gl.uniform1f(target.ratio, this.ratio)
  }

  private applyPass(gl: WebGLRenderingContext, target: ProgramBindings, pass: DrawPass) {
    gl.uniform3f(target.widths, pass.widths[0], pass.widths[1], pass.widths[2])
    gl.uniform3f(target.alphas, pass.alphas[0], pass.alphas[1], pass.alphas[2])
    gl.uniform1f(target.innerEdge, pass.innerEdge)
    gl.uniform1f(target.whiteMix, pass.whiteMix)
  }

  private enableAttribute(gl: WebGLRenderingContext, program: WebGLProgram, name: string, size: number, stride: number, offset: number) {
    const location = gl.getAttribLocation(program, name)
    if (location < 0) return
    gl.enableVertexAttribArray(location)
    gl.vertexAttribPointer(location, size, gl.FLOAT, false, stride, offset)
  }

  private renderCanvas(context: CanvasRenderingContext2D, curves: RenderConnection[], viewport: ConnectionViewport) {
    context.setTransform(1, 0, 0, 1, 0, 0)
    context.clearRect(0, 0, this.canvas.width, this.canvas.height)
    const ordered = [...curves.filter(curve => !curve.active), ...curves.filter(curve => curve.active)]
    for (const curve of ordered) {
      const start = this.toPixels(curve.start, viewport)
      const end = this.toPixels(curve.end, viewport)
      const distance = controlDistance(curve.start, curve.end) * viewport.zoom * this.ratio
      context.save()
      context.beginPath()
      context.moveTo(start.x, start.y)
      context.bezierCurveTo(start.x + distance, start.y, end.x - distance, end.y, end.x, end.y)
      context.strokeStyle = curve.color
      context.globalAlpha = curve.active ? .94 : curve.dimmed ? .18 : .74
      context.lineWidth = (curve.active ? 3.5 : curve.dimmed ? 1.5 : 2.15) * viewport.zoom * this.ratio
      context.lineCap = 'round'
      context.lineJoin = 'round'
      context.shadowColor = curve.color
      context.shadowBlur = (curve.active ? 12 : curve.dimmed ? 1 : 3) * viewport.zoom * this.ratio
      context.stroke()
      context.shadowBlur = 0
      context.globalAlpha = curve.active ? .58 : curve.dimmed ? .08 : .34
      context.lineWidth = .65 * viewport.zoom * this.ratio
      context.strokeStyle = '#ffffff'
      context.stroke()
      context.restore()
    }
  }

  private toPixels(point: ConnectionPoint, viewport: ConnectionViewport) {
    return {
      x: (viewport.x + point.x * viewport.zoom) * this.ratio,
      y: (viewport.y + point.y * viewport.zoom) * this.ratio
    }
  }
}
