import type { ApexOptions } from 'apexcharts'

import ThemedApexChart from './ThemedApexChart'
import { useChartTheme } from './useChartTheme'

const tooltipCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
})

function formatCompact(value: number): string {
  if (Math.abs(value) >= 1000) {
    return `R$ ${(value / 1000).toFixed(1).replace('.0', '')}k`
  }
  return new Intl.NumberFormat('pt-BR', {
    style: 'currency',
    currency: 'BRL',
    maximumFractionDigits: 0,
  }).format(value)
}

export interface SpendingPaceDayPoint {
  day: number
  currentMonth: number | null
  previousMonth: number
  threeMonthAverage: number
}

export interface SpendingPaceChartProps {
  points: SpendingPaceDayPoint[]
  labels?: {
    currentMonth: string
    previousMonth: string
    threeMonthAverage: string
    dayPrefix: string
  }
  height?: number
}

export function SpendingPaceChart({
  points,
  labels = {
    currentMonth: 'Mês atual',
    previousMonth: 'Mês passado',
    threeMonthAverage: 'Média (3 meses)',
    dayPrefix: 'Dia',
  },
  height = 280,
}: SpendingPaceChartProps) {
  const theme = useChartTheme()

  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'line',
      foreColor: theme.text,
      toolbar: { show: false },
      zoom: { enabled: false },
    },
    stroke: {
      curve: 'smooth',
      width: [3, 2, 2],
      dashArray: [0, 5, 3],
    },
    colors: [theme.expense, theme.primary, theme.gray],
    markers: {
      size: [0, 0, 0],
      hover: { size: 5 },
    },
    xaxis: {
      categories: points.map((p) => String(p.day)),
      tickAmount: 10,
      labels: { style: { colors: theme.text } },
      axisBorder: { color: theme.grid },
      axisTicks: { color: theme.grid },
    },
    yaxis: {
      labels: {
        style: { colors: theme.text },
        formatter: (val) => formatCompact(Number(val)),
      },
    },
    grid: { borderColor: theme.grid },
    legend: {
      show: true,
      position: 'top',
      horizontalAlign: 'center',
      labels: { colors: theme.text },
    },
    tooltip: {
      theme: theme.mode,
      shared: true,
      intersect: false,
      x: {
        formatter: (val) => `${labels.dayPrefix} ${val}`,
      },
      y: {
        formatter: (val) => (val === null || val === undefined ? '—' : tooltipCurrencyFormatter.format(Number(val))),
      },
    },
  })

  return (
    <ThemedApexChart
      type="line"
      height={height}
      series={[
        { name: labels.currentMonth, data: points.map((p) => p.currentMonth) },
        { name: labels.previousMonth, data: points.map((p) => p.previousMonth) },
        { name: labels.threeMonthAverage, data: points.map((p) => p.threeMonthAverage) },
      ]}
      getOptions={getOptions}
    />
  )
}
