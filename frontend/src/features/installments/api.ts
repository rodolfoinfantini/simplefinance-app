import { useQuery } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

export interface InstallmentPlanItem {
  id: string
  accountId: string
  accountName: string
  institutionName: string | null
  description: string
  categoryId: string | null
  categoryName: string | null
  categoryIcon: string | null
  categoryColor: string | null
  currentInstallment: number
  totalInstallments: number
  remainingInstallments: number
  installmentAmount: number
  paidAmount: number
  remainingAmount: number
  totalAmount: number
  purchaseDate: string
  nextInstallmentDate: string
  lastInstallmentDate: string
}

export interface InstallmentsSummaryResponse {
  totalRemainingAmount: number
  monthlyTotal: number
  activeCount: number
  items: InstallmentPlanItem[]
}

export function useInstallmentsQuery(accessToken: string, accountId?: string) {
  const query = accountId ? `?accountId=${encodeURIComponent(accountId)}` : ''
  return useQuery({
    queryKey: ['installments', accountId ?? 'all'],
    queryFn: () => apiFetch<InstallmentsSummaryResponse>(`/api/installments${query}`, { accessToken }),
    enabled: !!accessToken,
  })
}
