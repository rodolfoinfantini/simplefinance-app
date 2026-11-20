import type { ApexOptions } from 'apexcharts'

import ThemedApexChart from './ThemedApexChart'
import { useChartTheme } from './useChartTheme'

const monthFormatter = new Intl.DateTimeFormat('pt-BR', {
  month: 'short',
  year: '2-digit',
})

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

export interface ParentCategoryTrendMonth {
  year: number
  month: number
}

export interface ParentCategoryTrendSeriesItem {
  categoryId: string | null
  label: string
  color: string | null
  amounts: number[]
}

export interface ParentCategoryTrendChartProps {
  months: ParentCategoryTrendMonth[]
  series: ParentCategoryTrendSeriesItem[]
  height?: number
}

export function ParentCategoryTrendChart({
  months,
  series,
  height = 300,
}: ParentCategoryTrendChartProps) {
  const theme = useChartTheme()
  const fallbackPalette = theme.palette.length > 0 ? theme.palette : [theme.primary, theme.gray]

  const colors = series.map((item, idx) =>
    item.color ?? (item.categoryId === null ? theme.gray : fallbackPalette[idx % fallbackPalette.length])
  )

  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'bar',
      stacked: true,
      foreColor: theme.text,
      toolbar: { show: false },
    },
    plotOptions: {
      bar: {
        columnWidth: '55%',
        borderRadius: 3,
      },
    },
    colors,
    dataLabels: { enabled: false },
    xaxis: {
      categories: months.map((m) => monthFormatter.format(new Date(m.year, m.month - 1, 1))),
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
      position: 'bottom',
      horizontalAlign: 'center',
      labels: { colors: theme.text },
    },
    tooltip: {
      theme: theme.mode,
      shared: true,
      intersect: false,
      y: { formatter: (val) => tooltipCurrencyFormatter.format(Number(val)) },
    },
  })

  return (
    <ThemedApexChart
      type="bar"
      height={height}
      series={series.map((s) => ({ name: s.label, data: s.amounts }))}
      getOptions={getOptions}
    />
  )
}
