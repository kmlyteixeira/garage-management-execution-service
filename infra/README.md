# Execution Service AWS

Provisiona o Secret e o Deployment/Service do Execution Service no EKS
existente. O MongoDB persistente é provisionado pela plataforma.

No Environment `aws-lab` do GitHub, configure as Variables
`AWS_REGION` e `TERRAFORM_ENVIRONMENT`. Configure o Secret
`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` e `AWS_SESSION_TOKEN`. O workflow busca automaticamente o EKS e a string
de conexão MongoDB no SSM. O `garage-management-database` deve ter sido
aplicado antes.

O state usa o bucket S3 criado automaticamente pelo `infra` e a chave
`execution/terraform.tfstate`.
O workflow valida Terraform em PRs e aplica somente no `master`. Não versionar
senhas ou arquivos `.tfvars` reais.
