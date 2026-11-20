import type { ApexOptions } from 'apexcharts'

import type { IncomeExpenseMonthItem } from './IncomeExpenseBarChart'
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

export interface SavingsRateChartProps {
  items: IncomeExpenseMonthItem[]
  netLabel?: string
  savingsRateLabel?: string
  height?: number
}

export function SavingsRateChart({
  items,
  netLabel = 'Saldo líquido',
  savingsRateLabel = 'Taxa de poupança',
  height = 280,
}: SavingsRateChartProps) {
  const theme = useChartTheme()

  const netData = items.map((item) => Number((item.income - item.expense).toFixed(2)))
  const rateData = items.map((item) => {
    if (item.income <= 0) return 0
    return Number((((item.income - item.expense) / item.income) * 100).toFixed(1))
  })

  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'line',
      foreColor: theme.text,
      toolbar: { show: false },
      zoom: { enabled: false },
    },
    stroke: {
      width: [0, 3],
      curve: 'smooth',
    },
    plotOptions: {
      bar: {
        columnWidth: '52%',
        borderRadius: 4,
        colors: {
          ranges: [
            { from: -1000000000, to: -0.001, color: theme.expense },
            { from: 0, to: 1000000000, color: theme.income },
          ],
        },
      },
    },
    colors: [theme.income, theme.primary],
    markers: {
      size: [0, 4],
    },
    dataLabels: { enabled: false },
    xaxis: {
      categories: items.map((item) => monthFormatter.format(new Date(item.year, item.month - 1, 1))),
      labels: { style: { colors: theme.text } },
      axisBorder: { color: theme.grid },
      axisTicks: { color: theme.grid },
    },
    yaxis: [
      {
        labels: {
          style: { colors: theme.text },
          formatter: (val) => formatCompact(Number(val)),
        },
      },
      {
        opposite: true,
        labels: {
          style: { colors: theme.text },
          formatter: (val) => `${Math.round(Number(val))}%`,
        },
      },
    ],
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
      y: {
        formatter: (val, opts) => {
          const num = Number(val)
          if (opts?.seriesIndex === 1) {
            return `${num > 0 ? '+' : ''}${num.toFixed(1)}%`
          }
          return tooltipCurrencyFormatter.format(num)
        },
      },
    },
  })

  return (
    <ThemedApexChart
      type="line"
      height={height}
      series={[
        { name: netLabel, type: 'column', data: netData },
        { name: savingsRateLabel, type: 'line', data: rateData },
      ]}
      getOptions={getOptions}
    />
  )
}
