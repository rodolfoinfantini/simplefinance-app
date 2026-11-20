import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring v1's exact GET /api/accounts and
 * PATCH /api/accounts/{id}/hide response shape (frontend/src/features/accounts/api.ts) —
 * no `any`, so a backend contract change surfaces here as a type error.
 */

export type AccountType = 'Bank' | 'Credit'
export type AccountSubtype = 'CheckingAccount' | 'SavingsAccount' | 'CreditCard'

export interface CreditCardResponse {
  brand: string | null
  level: string | null
  brandAdditionalInfo: string | null
  creditLimit: number | null
  availableCredit: number | null
  balanceCloseDate: string | null
  balanceDueDate: string | null
  minimumPayment: number | null
  lastStatementBalance: number | null
  nextStatementBalance?: number | null
  status: string
  holderType: string | null
}

export interface AccountResponse {
  id: string
  connectionId: string
  institutionName: string | null
  type: AccountType
  subtype: AccountSubtype
  name: string
  marketingName: string | null
  numberMasked: string | null
  balance: number
  automaticallyInvestedBalance?: number | null
  currencyCode: string
  isHidden: boolean
  creditCard: CreditCardResponse | null
}

export interface ToggleAccountHideRequest {
  accessToken: string
  id: string
  isHidden: boolean
}

export function formatAccountOptionLabel(account: AccountResponse): string {
  const base = account.institutionName ?? account.name
  const kind = account.type === 'Credit' ? 'Cartão' : 'Conta'
  return account.numberMasked ? `${base} — ${kind} (${account.numberMasked})` : `${base} — ${kind}`
}

export function useAccountsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['accounts', accessToken],
    queryFn: () => apiFetch<AccountResponse[]>('/api/accounts', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useToggleAccountHideMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isHidden }: ToggleAccountHideRequest) =>
      apiFetch<AccountResponse>(`/api/accounts/${id}/hide`, {
        method: 'PATCH',
        body: { isHidden },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['accounts'] })
    },
  })
}
